import os

from pydantic_settings import BaseSettings


class Settings(BaseSettings):
    DEFAULT_LANGUAGE: str = os.environ.get("DEFAULT_LANGUAGE", default="de")


# Init the settings of the application on startup
settings: Settings = Settings()