FROM python:3.12-slim

WORKDIR /tests

# Install dependencies
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# Copy test files
COPY . .

# Run pytest by default; exit code propagates to docker run / compose
ENTRYPOINT ["pytest", "-v", "--tb=short"]
