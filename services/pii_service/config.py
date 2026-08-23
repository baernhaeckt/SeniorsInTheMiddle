import os

from pydantic_settings import BaseSettings


class Settings(BaseSettings):
    """Environment-backed configuration for the PII service."""

    DEFAULT_LANGUAGE: str = os.environ.get("DEFAULT_LANGUAGE", default="de")

    # Findings scoring below this are reported as near misses, not as detections. The dotnet
    # side reads the same variable so the dashboard can say which line was drawn.
    PII_SCORE_THRESHOLD: float = float(os.environ.get("PII_SCORE_THRESHOLD", default="0.6"))


# Init the settings of the application on startup
settings: Settings = Settings()