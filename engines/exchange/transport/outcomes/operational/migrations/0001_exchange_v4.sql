-- M06.12b: proposed operational persistence, V4 journal only.
-- Execute once, in a transaction, with ON_ERROR_STOP. No runtime DDL.
-- Do not import development rows or bind a journal implicitly.
CREATE SCHEMA qd_exchange_v4;
REVOKE ALL ON SCHEMA qd_exchange_v4 FROM PUBLIC;

CREATE TABLE qd_exchange_v4.schema_version (
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    version integer NOT NULL CHECK (version = 1),
    installed_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
INSERT INTO qd_exchange_v4.schema_version(version) VALUES (1);

-- One epoch per operational database. No rollover/reset in this migration.
CREATE TABLE qd_exchange_v4.journal_binding (
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    journal_epoch uuid NOT NULL UNIQUE CHECK (journal_epoch <> '00000000-0000-0000-0000-000000000000'),
    observed_head bigint NOT NULL DEFAULT 0 CHECK (observed_head >= 0),
    last_command_sequence bigint NOT NULL DEFAULT 0 CHECK (last_command_sequence BETWEEN 0 AND observed_head),
    bound_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

-- Global command namespace, shared by registration, submission, cancellation.
-- Payload is the original serialized V1 request body for the corresponding V2 RPC.
-- decision_payload is the verified serialized V2 DecisionRecord, not an RPC status.
CREATE TABLE qd_exchange_v4.commands (
    command_id uuid PRIMARY KEY CHECK (command_id <> '00000000-0000-0000-0000-000000000000'),
    journal_epoch uuid NOT NULL REFERENCES qd_exchange_v4.journal_binding(journal_epoch),
    kind text NOT NULL CHECK (kind IN ('register','submit','cancel')),
    owner_user_id uuid CHECK (owner_user_id <> '00000000-0000-0000-0000-000000000000'),
    order_id uuid CHECK (order_id <> '00000000-0000-0000-0000-000000000000'),
    target_submission_id uuid,
    admission_reference text,
    risk_policy_version text,
    payload bytea NOT NULL CHECK (octet_length(payload) BETWEEN 1 AND 65536),
    phase text NOT NULL DEFAULT 'pending' CHECK (phase IN ('pending','reserved','accepted','rejected')),
    decision_sequence bigint CHECK (decision_sequence > 0),
    decision_payload bytea CHECK (octet_length(decision_payload) BETWEEN 1 AND 1048576),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT command_kind_fields CHECK (
       (kind = 'register' AND owner_user_id IS NULL AND order_id IS NULL AND target_submission_id IS NULL
            AND admission_reference IS NULL AND risk_policy_version IS NULL AND phase <> 'reserved') OR
       (kind = 'submit' AND owner_user_id IS NOT NULL AND order_id IS NOT NULL AND target_submission_id IS NULL
            AND admission_reference IS NOT NULL AND length(btrim(admission_reference)) BETWEEN 1 AND 256
            AND risk_policy_version IS NOT NULL AND length(btrim(risk_policy_version)) BETWEEN 1 AND 128) OR
       (kind = 'cancel' AND owner_user_id IS NOT NULL AND order_id IS NOT NULL AND target_submission_id IS NOT NULL
            AND admission_reference IS NULL AND risk_policy_version IS NULL AND phase <> 'reserved')
    ),
    CONSTRAINT command_outcome_fields CHECK (
       (phase IN ('pending','reserved') AND decision_sequence IS NULL AND decision_payload IS NULL) OR
       (phase IN ('accepted','rejected') AND decision_sequence IS NOT NULL AND decision_payload IS NOT NULL)
    ),
    UNIQUE (journal_epoch, decision_sequence),
    UNIQUE (command_id, order_id, owner_user_id, journal_epoch),
    FOREIGN KEY (target_submission_id, order_id, owner_user_id, journal_epoch)
        REFERENCES qd_exchange_v4.commands(command_id, order_id, owner_user_id, journal_epoch)
);
CREATE UNIQUE INDEX one_submission_per_order ON qd_exchange_v4.commands(order_id) WHERE kind = 'submit';
-- A rejected cancellation may be followed by a distinct cancellation intent.
CREATE UNIQUE INDEX one_unresolved_or_accepted_cancel_per_order ON qd_exchange_v4.commands(order_id)
    WHERE kind = 'cancel' AND phase IN ('pending','accepted');
CREATE INDEX pending_commands ON qd_exchange_v4.commands(journal_epoch, created_at, command_id)
    WHERE phase IN ('pending','reserved');

CREATE FUNCTION qd_exchange_v4.guard_binding() RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'Journal binding cannot be deleted' USING ERRCODE='23514'; END IF;
    IF TG_OP = 'INSERT' THEN
        IF NEW.observed_head <> 0 OR NEW.last_command_sequence <> 0 THEN
            RAISE EXCEPTION 'New binding requires empty journal' USING ERRCODE='23514';
        END IF;
        RETURN NEW;
    END IF;
    IF NEW.journal_epoch <> OLD.journal_epoch OR NEW.singleton <> OLD.singleton OR NEW.bound_at <> OLD.bound_at
        OR NEW.observed_head < OLD.observed_head OR NEW.last_command_sequence < OLD.last_command_sequence
        OR NEW.last_command_sequence - OLD.last_command_sequence > 1 THEN
        RAISE EXCEPTION 'Journal identity or cursor regression/skip' USING ERRCODE='23514';
    END IF;
    IF NEW.last_command_sequence <> OLD.last_command_sequence AND NOT EXISTS (
        SELECT 1 FROM qd_exchange_v4.commands WHERE journal_epoch = NEW.journal_epoch
          AND decision_sequence = NEW.last_command_sequence AND phase IN ('accepted','rejected')
    ) THEN RAISE EXCEPTION 'Checkpoint requires recorded decision' USING ERRCODE='23514'; END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER journal_binding_guard BEFORE INSERT OR UPDATE OR DELETE ON qd_exchange_v4.journal_binding
    FOR EACH ROW EXECUTE FUNCTION qd_exchange_v4.guard_binding();

CREATE FUNCTION qd_exchange_v4.guard_command() RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'Durable commands cannot be deleted' USING ERRCODE='23514'; END IF;
    IF TG_OP = 'INSERT' THEN
        IF NEW.phase <> 'pending' THEN RAISE EXCEPTION 'Intent must start pending' USING ERRCODE='23514'; END IF;
        IF NEW.kind = 'cancel' AND NOT EXISTS (
            SELECT 1 FROM qd_exchange_v4.commands s WHERE s.command_id = NEW.target_submission_id
              AND s.kind = 'submit' AND s.phase = 'accepted' AND s.order_id = NEW.order_id
              AND s.owner_user_id = NEW.owner_user_id AND s.journal_epoch = NEW.journal_epoch
        ) THEN RAISE EXCEPTION 'Cancellation requires accepted owned submission' USING ERRCODE='23514'; END IF;
        RETURN NEW;
    END IF;
    IF ROW(NEW.command_id, NEW.journal_epoch, NEW.kind, NEW.owner_user_id, NEW.order_id,
        NEW.target_submission_id, NEW.admission_reference, NEW.risk_policy_version, NEW.payload, NEW.created_at)
        IS DISTINCT FROM ROW(OLD.command_id, OLD.journal_epoch, OLD.kind, OLD.owner_user_id, OLD.order_id,
        OLD.target_submission_id, OLD.admission_reference, OLD.risk_policy_version, OLD.payload, OLD.created_at) THEN
        RAISE EXCEPTION 'Command identity and payload are immutable' USING ERRCODE='23514';
    END IF;
    IF NEW.phase = OLD.phase AND NEW.decision_sequence IS NOT DISTINCT FROM OLD.decision_sequence
        AND NEW.decision_payload IS NOT DISTINCT FROM OLD.decision_payload THEN RETURN NEW; END IF;
    IF NOT ((OLD.kind = 'submit' AND OLD.phase = 'pending' AND NEW.phase = 'reserved') OR
        (OLD.kind = 'submit' AND OLD.phase = 'reserved' AND NEW.phase IN ('accepted','rejected')) OR
        (OLD.kind IN ('register','cancel') AND OLD.phase = 'pending' AND NEW.phase IN ('accepted','rejected'))) THEN
        RAISE EXCEPTION 'Invalid command phase transition' USING ERRCODE='23514';
    END IF;
    IF NEW.decision_sequence IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM qd_exchange_v4.journal_binding b WHERE b.journal_epoch = NEW.journal_epoch
          AND NEW.decision_sequence > b.last_command_sequence AND NEW.decision_sequence <= b.observed_head
    ) THEN RAISE EXCEPTION 'Decision outside observed unsettled journal range' USING ERRCODE='23514'; END IF;
    IF NEW.kind = 'cancel' AND NEW.decision_sequence IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM qd_exchange_v4.commands s WHERE s.command_id = NEW.target_submission_id
          AND s.decision_sequence < NEW.decision_sequence
    ) THEN RAISE EXCEPTION 'Cancellation must follow submission' USING ERRCODE='23514'; END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER command_guard BEFORE INSERT OR UPDATE OR DELETE ON qd_exchange_v4.commands
    FOR EACH ROW EXECUTE FUNCTION qd_exchange_v4.guard_command();

-- Explicit provisioning ONLY. Caller must verify authenticated engine status
-- and that this is an empty journal; these functions cannot interrogate gRPC.
CREATE FUNCTION qd_exchange_v4.bind_empty_journal(p_epoch uuid, p_format integer, p_healthy boolean, p_head bigint)
RETURNS void LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
    IF p_epoch IS NULL OR p_format IS DISTINCT FROM 4 OR p_healthy IS DISTINCT FROM true OR p_head IS DISTINCT FROM 0::bigint THEN
        RAISE EXCEPTION 'Provisioning requires healthy empty V4 journal' USING ERRCODE='23514';
    END IF;
    INSERT INTO qd_exchange_v4.journal_binding(journal_epoch) VALUES (p_epoch);
END $$;

-- Persist the highest authenticated engine watermark ever observed. This is
-- a fence, NOT proof the journal is intact or the financial effects are applied.
CREATE FUNCTION qd_exchange_v4.observe_engine(p_epoch uuid, p_format integer, p_healthy boolean, p_head bigint)
RETURNS bigint LANGUAGE plpgsql SET search_path = pg_catalog AS $$
DECLARE b qd_exchange_v4.journal_binding%ROWTYPE;
BEGIN
    SELECT * INTO b FROM qd_exchange_v4.journal_binding WHERE singleton FOR UPDATE;
    IF NOT FOUND OR p_epoch IS DISTINCT FROM b.journal_epoch OR p_format IS DISTINCT FROM 4
       OR p_healthy IS DISTINCT FROM true OR p_head IS NULL OR p_head < b.observed_head THEN
        RAISE EXCEPTION 'Engine not ready: missing binding, epoch/format mismatch, unhealthy or regressed head' USING ERRCODE='23514';
    END IF;
    UPDATE qd_exchange_v4.journal_binding SET observed_head=p_head WHERE singleton;
    RETURN b.last_command_sequence;
END $$;

-- Trusted consumer must call AFTER all M07 effects durably commit; a retry
-- after effects commit but before cursor commit must replay the same decision.
CREATE FUNCTION qd_exchange_v4.advance_checkpoint(p_epoch uuid, p_previous bigint, p_next bigint)
RETURNS void LANGUAGE plpgsql SET search_path = pg_catalog AS $$
DECLARE b qd_exchange_v4.journal_binding%ROWTYPE;
BEGIN
    SELECT * INTO b FROM qd_exchange_v4.journal_binding WHERE singleton FOR UPDATE;
    IF NOT FOUND OR p_epoch IS DISTINCT FROM b.journal_epoch OR p_previous IS DISTINCT FROM b.last_command_sequence
       OR p_next IS NULL OR p_next <= b.last_command_sequence OR p_next - b.last_command_sequence <> 1
       OR p_next > b.observed_head THEN RAISE EXCEPTION 'Checkpoint compare-and-swap or sequence failed' USING ERRCODE='23514'; END IF;
    UPDATE qd_exchange_v4.journal_binding SET last_command_sequence=p_next WHERE singleton;
END $$;

REVOKE ALL ON ALL TABLES IN SCHEMA qd_exchange_v4 FROM PUBLIC;
REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA qd_exchange_v4 FROM PUBLIC;
-- Deployment role grants intentionally omitted until service composition review.
