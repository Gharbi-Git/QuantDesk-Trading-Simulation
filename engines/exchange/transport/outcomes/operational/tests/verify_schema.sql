\set ON_ERROR_STOP on
BEGIN;
SET LOCAL lock_timeout = '5s';
DO $$ BEGIN
    IF current_database() <> 'quantdesk_test' THEN RAISE EXCEPTION 'Test database required'; END IF;
    IF to_regnamespace('qd_exchange_v4') IS NOT NULL THEN RAISE EXCEPTION 'Operational schema already exists; refusing to modify it'; END IF;
END $$;
\ir ../migrations/0001_exchange_v4.sql

CREATE FUNCTION pg_temp.expect_error(statement text, expected_state text) RETURNS void LANGUAGE plpgsql AS $$
BEGIN
    BEGIN
        EXECUTE statement;
    EXCEPTION WHEN OTHERS THEN
        IF SQLSTATE = expected_state THEN RETURN; END IF;
        RAISE;
    END;
    RAISE EXCEPTION 'Unexpected success: %', statement;
END $$;
CREATE FUNCTION pg_temp.assert_true(value boolean, message text) RETURNS void LANGUAGE plpgsql AS $$
BEGIN
    IF value IS DISTINCT FROM true THEN RAISE EXCEPTION '%', message; END IF;
END $$;

-- Synthetic identifiers/opaque bytes: schema constraints only, NOT real orders.
SELECT pg_temp.expect_error($q$SELECT qd_exchange_v4.observe_engine('10000000-0000-4000-8000-000000000001',4,true,0)$q$, '23514');
SELECT pg_temp.expect_error($q$SELECT qd_exchange_v4.bind_empty_journal('10000000-0000-4000-8000-000000000001',3,true,0)$q$, '23514');
SELECT pg_temp.expect_error($q$SELECT qd_exchange_v4.bind_empty_journal('10000000-0000-4000-8000-000000000001',4,true,1)$q$, '23514');
SELECT qd_exchange_v4.bind_empty_journal('10000000-0000-4000-8000-000000000001',4,true,0);
SELECT pg_temp.expect_error($q$SELECT qd_exchange_v4.bind_empty_journal('10000000-0000-4000-8000-000000000002',4,true,0)$q$, '23505');
SELECT pg_temp.expect_error($q$SELECT qd_exchange_v4.observe_engine('10000000-0000-4000-8000-000000000002',4,true,0)$q$, '23514');
SELECT pg_temp.expect_error($q$SELECT qd_exchange_v4.observe_engine('10000000-0000-4000-8000-000000000001',4,false,0)$q$, '23514');
SELECT pg_temp.assert_true(qd_exchange_v4.observe_engine('10000000-0000-4000-8000-000000000001',4,true,4)=0,'Initial cursor');
SELECT pg_temp.expect_error($q$SELECT qd_exchange_v4.observe_engine('10000000-0000-4000-8000-000000000001',4,true,3)$q$, '23514');
SELECT pg_temp.expect_error($q$UPDATE qd_exchange_v4.journal_binding SET journal_epoch='10000000-0000-4000-8000-000000000002'$q$, '23514');
SELECT pg_temp.expect_error($q$DELETE FROM qd_exchange_v4.journal_binding$q$, '23514');
\echo PASS: explicit V4 provisioning, epoch/health and observed-head fencing

INSERT INTO qd_exchange_v4.commands(command_id,journal_epoch,kind,payload) VALUES
 ('20000000-0000-4000-8000-000000000001','10000000-0000-4000-8000-000000000001','register',decode('01','hex'));
INSERT INTO qd_exchange_v4.commands(command_id,journal_epoch,kind,owner_user_id,order_id,admission_reference,risk_policy_version,payload) VALUES
 ('20000000-0000-4000-8000-000000000002','10000000-0000-4000-8000-000000000001','submit','30000000-0000-4000-8000-000000000001','40000000-0000-4000-8000-000000000001','fixture-admission','fixture-risk-v1',decode('02','hex'));
SELECT pg_temp.expect_error($q$INSERT INTO qd_exchange_v4.commands(command_id,journal_epoch,kind,payload) VALUES ('20000000-0000-4000-8000-000000000002','10000000-0000-4000-8000-000000000001','register',decode('03','hex'))$q$, '23505');
SELECT pg_temp.expect_error($q$UPDATE qd_exchange_v4.commands SET payload=decode('ff','hex') WHERE kind='submit'$q$, '23514');
SELECT pg_temp.expect_error($q$UPDATE qd_exchange_v4.commands SET phase='accepted',decision_sequence=2,decision_payload=decode('02','hex') WHERE kind='submit'$q$, '23514');
SELECT pg_temp.expect_error($q$INSERT INTO qd_exchange_v4.commands(command_id,journal_epoch,kind,owner_user_id,order_id,target_submission_id,payload) VALUES ('20000000-0000-4000-8000-000000000003','10000000-0000-4000-8000-000000000001','cancel','30000000-0000-4000-8000-000000000001','40000000-0000-4000-8000-000000000001','20000000-0000-4000-8000-000000000002',decode('03','hex'))$q$, '23514');
UPDATE qd_exchange_v4.commands SET phase='accepted',decision_sequence=1,decision_payload=decode('11','hex') WHERE kind='register';
UPDATE qd_exchange_v4.commands SET phase='reserved' WHERE kind='submit';
SELECT pg_temp.expect_error($q$UPDATE qd_exchange_v4.commands SET phase='accepted',decision_sequence=5,decision_payload=decode('12','hex') WHERE kind='submit'$q$, '23514');
SELECT pg_temp.expect_error($q$UPDATE qd_exchange_v4.commands SET phase='accepted',decision_sequence=1,decision_payload=decode('12','hex') WHERE kind='submit'$q$, '23505');
UPDATE qd_exchange_v4.commands SET phase='accepted',decision_sequence=2,decision_payload=decode('12','hex') WHERE kind='submit';
SELECT pg_temp.expect_error($q$UPDATE qd_exchange_v4.commands SET phase='rejected' WHERE kind='submit'$q$, '23514');
SELECT pg_temp.expect_error($q$DELETE FROM qd_exchange_v4.commands WHERE kind='submit'$q$, '23514');
\echo PASS: global command identity, immutable payloads, reserved-before-submit and immutable decisions

SELECT pg_temp.expect_error($q$INSERT INTO qd_exchange_v4.commands(command_id,journal_epoch,kind,owner_user_id,order_id,target_submission_id,payload) VALUES ('20000000-0000-4000-8000-000000000003','10000000-0000-4000-8000-000000000001','cancel','30000000-0000-4000-8000-000000000002','40000000-0000-4000-8000-000000000001','20000000-0000-4000-8000-000000000002',decode('03','hex'))$q$, '23514');
INSERT INTO qd_exchange_v4.commands(command_id,journal_epoch,kind,owner_user_id,order_id,target_submission_id,payload) VALUES
 ('20000000-0000-4000-8000-000000000003','10000000-0000-4000-8000-000000000001','cancel','30000000-0000-4000-8000-000000000001','40000000-0000-4000-8000-000000000001','20000000-0000-4000-8000-000000000002',decode('03','hex'));
SELECT pg_temp.expect_error($q$INSERT INTO qd_exchange_v4.commands(command_id,journal_epoch,kind,owner_user_id,order_id,target_submission_id,payload) VALUES ('20000000-0000-4000-8000-000000000004','10000000-0000-4000-8000-000000000001','cancel','30000000-0000-4000-8000-000000000001','40000000-0000-4000-8000-000000000001','20000000-0000-4000-8000-000000000002',decode('04','hex'))$q$, '23505');
UPDATE qd_exchange_v4.commands SET phase='rejected',decision_sequence=3,decision_payload=decode('13','hex') WHERE command_id='20000000-0000-4000-8000-000000000003';
INSERT INTO qd_exchange_v4.commands(command_id,journal_epoch,kind,owner_user_id,order_id,target_submission_id,payload) VALUES
 ('20000000-0000-4000-8000-000000000004','10000000-0000-4000-8000-000000000001','cancel','30000000-0000-4000-8000-000000000001','40000000-0000-4000-8000-000000000001','20000000-0000-4000-8000-000000000002',decode('04','hex'));
\echo PASS: cancellation owner, accepted submission and duplicate-intent constraints

SELECT pg_temp.expect_error($q$SELECT qd_exchange_v4.advance_checkpoint('10000000-0000-4000-8000-000000000001',0,2)$q$, '23514');
SELECT qd_exchange_v4.advance_checkpoint('10000000-0000-4000-8000-000000000001',0,1);
SELECT pg_temp.expect_error($q$SELECT qd_exchange_v4.advance_checkpoint('10000000-0000-4000-8000-000000000001',0,1)$q$, '23514');
SELECT qd_exchange_v4.advance_checkpoint('10000000-0000-4000-8000-000000000001',1,2);
SELECT qd_exchange_v4.advance_checkpoint('10000000-0000-4000-8000-000000000001',2,3);
SELECT pg_temp.expect_error($q$SELECT qd_exchange_v4.advance_checkpoint('10000000-0000-4000-8000-000000000001',3,4)$q$, '23514');
UPDATE qd_exchange_v4.commands SET phase='accepted',decision_sequence=4,decision_payload=decode('14','hex') WHERE command_id='20000000-0000-4000-8000-000000000004';
SELECT qd_exchange_v4.advance_checkpoint('10000000-0000-4000-8000-000000000001',3,4);
SELECT pg_temp.assert_true(qd_exchange_v4.observe_engine('10000000-0000-4000-8000-000000000001',4,true,4)=4,'Recovered cursor');
SELECT pg_temp.expect_error($q$UPDATE qd_exchange_v4.journal_binding SET last_command_sequence=0$q$, '23514');
SELECT pg_temp.expect_error($q$SELECT qd_exchange_v4.observe_engine('10000000-0000-4000-8000-000000000001',4,true,3)$q$, '23514');
\echo PASS: ordered checkpoints, missing decision, stale cursor and regressed journal blocked
ROLLBACK;
DO $$ BEGIN IF to_regnamespace('qd_exchange_v4') IS NOT NULL THEN RAISE EXCEPTION 'Expected migration rollback'; END IF; END $$;
\echo M06 OPERATIONAL SCHEMA SMOKE PASSED - ROLLED BACK
