from dataclasses import dataclass, is_dataclass, asdict
from typing import Any


@dataclass(frozen=True)
class DetectionResultItem:
    """
    PII detection result item.
    """
    information_type: str
    entity_type: str
    score: float = 0.0
    start_position: int = 0
    end_position: int = 0
    detected_text: str = ""
    replacement_text: str = ""
    risk_level: int = 0
    hipaa_category: str = ""

@dataclass(frozen=True)
class DetectionResult:
    """
    PII detection result.
    """
    detection_results: list[DetectionResultItem]


def to_dict(dataclass_instance: Any) -> dict:
    """
    Recursively converts a dataclass or other object to a dictionary.
    Args:
        dataclass_instance (Any): The dataclass instance or object to convert.s
    Returns:
        dict: The converted dictionary.
    """
    if is_dataclass(dataclass_instance):
        return {k: to_dict(v) for k, v in asdict(dataclass_instance).items()}
    if isinstance(dataclass_instance, dict):
        return {str(k): to_dict(v) for k, v in dataclass_instance.items()}
    if isinstance(dataclass_instance, (list, tuple, set)):
        return [to_dict(v) for v in dataclass_instance]
    return dataclass_instance
