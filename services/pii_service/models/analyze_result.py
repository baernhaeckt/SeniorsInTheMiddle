from dataclasses import dataclass

from services.pii_service.models.detection_result import DetectionResultItem


@dataclass(frozen=True)
class AnalyzeResult:
    """
    PII analysis result.
    """
    detection_results: list[DetectionResultItem]
    detection_count: int
    risk_score_mean: float
    risk_score_median: float
    detected_pii_types: list[str]
    detected_pii_type_frequencies: dict[str, int]