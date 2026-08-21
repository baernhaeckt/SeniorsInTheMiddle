from dataclasses import dataclass


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
    risk_level: int = 0
    hipaa_category: str = ""

@dataclass(frozen=True)
class DetectionResult:
    """
    PII detection result.
    """
    detection_results: list[DetectionResultItem]
