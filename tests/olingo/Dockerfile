# ── Stage 1: dependency cache ─────────────────────────────────────────────────
# Download all Maven dependencies before copying test sources so that Docker's
# layer cache is reused on subsequent builds when only test code changes.
FROM maven:3-eclipse-temurin-21 AS deps

WORKDIR /build

# Copy only the POM first -- this layer is invalidated only when pom.xml changes
COPY pom.xml .

# Resolve all compile + test dependencies (fail fast if Maven central is unreachable)
RUN mvn dependency:go-offline -B --no-transfer-progress

# ── Stage 2: test runner ──────────────────────────────────────────────────────
FROM maven:3-eclipse-temurin-21 AS test

WORKDIR /build

# Bring the pre-fetched local Maven repository from the deps stage
COPY --from=deps /root/.m2 /root/.m2

# Copy everything else -- pom.xml + src/
COPY . .

# ODATA_BASE_URL is injected at container runtime via docker-compose environment.
# Maven Surefire reads it as a system property (configured in pom.xml).
ENV ODATA_BASE_URL=http://localhost:8080

# Run tests; exit code propagates to docker-compose so a test failure fails the job.
CMD ["mvn", "test", "-B", "--no-transfer-progress", "-o"]
