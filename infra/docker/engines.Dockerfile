# Linux-only compilation / test verification; no engine production deployment yet.
FROM gcc:14-bookworm
RUN apt-get update && apt-get install -y --no-install-recommends cmake ninja-build \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /repo
COPY engines/ ./engines/
RUN cmake -S engines -B /build -G Ninja -DCMAKE_BUILD_TYPE=Release \
    && cmake --build /build \
    && ctest --test-dir /build --output-on-failure
CMD ["/build/backtest/quantdesk_backtest"]