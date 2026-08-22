"""Tests for PrivacyChecker.check_privacy_risk returning every name with its probability."""
import numpy as np
import pytest

from privacy_check_service.privacy_checker import PrivacyChecker


class StubEncoder:
    """Deterministic encoder: one 3-d vector per input string."""

    VECTORS = {
        "Hans Muster": np.array([1.0, 0.0, 0.0]),
        "Anna Beispiel": np.array([0.0, 1.0, 0.0]),
        "Peter Test": np.array([0.0, 0.0, 1.0]),
    }

    def encode(self, x):
        if isinstance(x, str):
            # Text embedding: closest to the second name.
            return np.array([0.2, 0.9, 0.1])
        return np.stack([self.VECTORS[name] for name in x])


TEXT = "Anna Beispiel hat heute angerufen."
NAMES = ["Hans Muster", "Anna Beispiel", "Peter Test"]


@pytest.fixture
def checker():
    instance = PrivacyChecker.__new__(PrivacyChecker)
    instance.encoder_model = StubEncoder()
    return instance


def test_returns_all_names_with_probability_in_input_order(checker, monkeypatch):
    captured = {}

    def fake_probability(features_list):
        captured["features"] = features_list
        return np.array([0.2, 0.7, 0.1])

    monkeypatch.setattr(PrivacyChecker, "_calculate_probability", staticmethod(fake_probability))

    result = checker.check_privacy_risk(TEXT, NAMES)

    assert result == [
        {"name": "Hans Muster", "risk_probability": pytest.approx(0.2)},
        {"name": "Anna Beispiel", "risk_probability": pytest.approx(0.7)},
        {"name": "Peter Test", "risk_probability": pytest.approx(0.1)},
    ]
    assert all(isinstance(r["risk_probability"], float) for r in result)

    features = captured["features"]
    assert isinstance(features, np.ndarray)
    assert features.shape == (3, 4)
    # Embedding similarity = name_vector @ text_vector.
    assert features[:, 0] == pytest.approx([0.2, 0.9, 0.1])
    # Only "Anna Beispiel" appears verbatim in the text.
    assert features[:, 1].tolist() == [0.0, 1.0, 0.0]


def test_make_features_exact_substring_sets_flag():
    similarities = np.array([0.2, 0.9, 0.1])

    hit = PrivacyChecker._make_features(similarities, TEXT, "anna beispiel", 0.9)
    miss = PrivacyChecker._make_features(similarities, TEXT, "Hans Muster", 0.2)

    assert len(hit) == 4
    assert hit[0] == 0.9
    assert hit[1] == 1.0
    assert 0.0 < hit[2] <= 1.0
    assert hit[3] == pytest.approx((0.9 - similarities.mean()) / similarities.std())
    assert miss[1] == 0.0


def test_make_features_single_candidate_has_zero_zscore():
    similarities = np.array([0.5])

    features = PrivacyChecker._make_features(similarities, TEXT, "Anna Beispiel", 0.5)

    assert features[3] == 0.0
