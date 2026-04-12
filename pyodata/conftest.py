"""
Pytest fixtures for OhData pyodata integration tests.

BASE_URL is read from the environment variable BASE_URL (default: http://localhost:8080).
The HTTP session is scoped to the test session for connection reuse.
"""

import os
import requests
import pytest


@pytest.fixture(scope="session")
def base_url() -> str:
    """Return the base URL of the OhData test bench server."""
    return os.environ.get("BASE_URL", "http://localhost:8080")


@pytest.fixture(scope="session")
def http_session() -> requests.Session:
    """Return a shared requests.Session for the test session."""
    session = requests.Session()
    session.headers.update({"Content-Type": "application/json"})
    yield session
    session.close()
