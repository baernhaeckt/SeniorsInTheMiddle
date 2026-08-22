from dataclasses import dataclass, field

from .detection_result import DetectionResultItem


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
    #: Findings that scored below the threshold. Never replaced; shown as "near misses".
    ignored_results: list[DetectionResultItem] = field(default_factory=list)
