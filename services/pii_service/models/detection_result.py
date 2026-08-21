from dataclasses import dataclass


@dataclass(frozen=True)
class DetectionResultItem:
    """
    PII detection result item.
    """
    entity_type: str
    score: float = 0.0
    start_position: int = 0
    end_position: int = 0

class DetectionResult:
    """
    PII detection result.
    """
    detection_results: list[DetectionResultItem]
