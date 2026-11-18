# MVP bootstrap binary; OCI sandbox supervision is NOT implemented by this image.
FROM rust:1.100.0-bookworm AS build
WORKDIR /repo
COPY rust-toolchain.toml ./
COPY services/strategy-runner/ ./services/strategy-runner/
WORKDIR /repo/services/strategy-runner
RUN cargo build --release --locked

FROM debian:bookworm-slim
COPY --from=build /repo/services/strategy-runner/target/release/quantdesk-strategy-runner /usr/local/bin/quantdesk-strategy-runner
USER 65532:65532
ENTRYPOINT ["/usr/local/bin/quantdesk-strategy-runner"]