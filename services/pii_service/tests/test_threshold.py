"""Tests for the PII_SCORE_THRESHOLD split in PiiDetector.analyze_text."""
from dataclasses import dataclass

import pytest

from pii_service import config
from pii_service.pii_detector import PiiDetector


@dataclass
class FakeRecognizerResult:
    """The few fields of a Presidio RecognizerResult that the detector actually reads."""

    entity_type: str
    score: float
    start: int
    end: int


class StubAnalyzer:
    """Returns canned results and records how it was called, standing in for Presidio."""

    def __init__(self, results):
        self.results = results
        self.calls = []

    def analyze(self, text, entities, language):
        self.calls.append({"text": text, "entities": entities, "language": language})
        return list(self.results)


TEXT = "Hans Muster wohnt in Bern, hans@example.com"
PERSON = FakeRecognizerResult("PERSON", 0.85, 0, 11)
LOCATION = FakeRecognizerResult("LOCATION", 0.4, 21, 25)
EMAIL = FakeRecognizerResult("EMAIL_ADDRESS", 1.0, 27, 43)


def make_detector(results):
    detector = PiiDetector.__new__(PiiDetector)
    detector.analyzer = StubAnalyzer(results)
    detector.language_code = "de"
    return detector


@pytest.fixture
def threshold(monkeypatch):
    monkeypatch.setattr(config.settings, "PII_SCORE_THRESHOLD", 0.6)
    return config.settings.PII_SCORE_THRESHOLD


def test_above_threshold_is_kept_with_mapping(threshold):
    detector = make_detector([PERSON, EMAIL])

    result = detector.analyze_text(TEXT, ["PERSON", "EMAIL_ADDRESS"])

    assert result["detection_count"] == 2
    assert result["ignored_results"] == []
    person, email = result["detection_results"]
    assert person == {
        "information_type": "Person",
        "entity_type": "PERSON",
        "score": 0.85,
        "start_position": 0,
        "end_position": 11,
        "detected_text": "Hans Muster",
        "risk_level": 3,
        "hipaa_category": "Protected Health Information",
    }
    assert email["information_type"] == "Email Address"
    assert email["entity_type"] == "EMAIL_ADDRESS"
    assert email["detected_text"] == "hans@example.com"
    assert email["risk_level"] == 3
    assert email["hipaa_category"] == "Protected Health Information"
    assert sorted(result["detected_pii_types"]) == ["EMAIL_ADDRESS", "PERSON"]
    assert result["detected_pii_type_frequencies"] == {"EMAIL_ADDRESS": 1, "PERSON": 1}
    assert result["risk_score_mean"] == pytest.approx(0.925)
    assert detector.analyzer.calls == [
        {"text": TEXT, "entities": ["PERSON", "EMAIL_ADDRESS"], "language": "de"}
    ]


def test_below_threshold_lands_in_ignored_results(threshold):
    detector = make_detector([PERSON, LOCATION])

    result = detector.analyze_text(TEXT, ["PERSON", "LOCATION"])

    assert [r["entity_type"] for r in result["detection_results"]] == ["PERSON"]
    assert result["detection_count"] == 1
    assert result["risk_score_mean"] == pytest.approx(0.85)
    assert len(result["ignored_results"]) == 1
    ignored = result["ignored_results"][0]
    # LOCATION resolves through the enum alias to ADDRESS.
    assert ignored["entity_type"] == "ADDRESS"
    assert ignored["information_type"] == "Address"
    assert ignored["risk_level"] == 2
    assert ignored["hipaa_category"] == "Not Protected Health Information"
    assert ignored["score"] == 0.4
    assert ignored["detected_text"] == "Bern"
    # Same item shape as a kept finding.
    assert set(ignored) == set(result["detection_results"][0])


def test_exactly_at_threshold_is_kept(threshold):
    detector = make_detector([FakeRecognizerResult("PERSON", 0.6, 0, 11)])

    result = detector.analyze_text(TEXT, ["PERSON"])

    assert result["detection_count"] == 1
    assert result["ignored_results"] == []


def test_all_below_threshold_returns_only_ignored_results(threshold):
    detector = make_detector([LOCATION, FakeRecognizerResult("PERSON", 0.1, 0, 11)])

    result = detector.analyze_text(TEXT, ["PERSON", "LOCATION"])

    assert set(result) == {"ignored_results"}
    assert [r["entity_type"] for r in result["ignored_results"]] == ["ADDRESS", "PERSON"]


def test_no_findings_returns_empty_dict(threshold):
    detector = make_detector([])

    assert detector.analyze_text(TEXT, ["PERSON"]) == {}


def test_threshold_is_read_from_settings(monkeypatch):
    finding = FakeRecognizerResult("PERSON", 0.8, 0, 11)

    monkeypatch.setattr(config.settings, "PII_SCORE_THRESHOLD", 0.6)
    kept = make_detector([finding]).analyze_text(TEXT, ["PERSON"])
    assert kept["detection_count"] == 1
    assert kept["ignored_results"] == []

    monkeypatch.setattr(config.settings, "PII_SCORE_THRESHOLD", 0.9)
    flipped = make_detector([finding]).analyze_text(TEXT, ["PERSON"])
    assert set(flipped) == {"ignored_results"}
    assert flipped["ignored_results"][0]["score"] == 0.8


def test_default_threshold_is_0_6():
    assert config.Settings.model_fields["PII_SCORE_THRESHOLD"].default == 0.6
