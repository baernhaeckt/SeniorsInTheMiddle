"""Tests that a replacement keeps the shape of the value it stands in for.

A replacement is spliced into a JSON string in the outgoing body. A line break
in one escapes to a literal \\n there, changes the shape of what the destination
receives, and reads as one in the payload inspector.
"""
import re

import pytest

from pii_service.pii_detector import PiiDetector, single_line
from pii_service.utils.pii_risk_mappings import PII_TYPE_MAPPINGS


@pytest.fixture
def detector():
    # create_replacement_text does not touch the analyzer, so skip building one.
    return PiiDetector.__new__(PiiDetector)


@pytest.mark.parametrize(
    "value, expected",
    [
        ("Bertha-Rogner-Ring 4951\n03884 Rudolstadt", "Bertha-Rogner-Ring 4951 03884 Rudolstadt"),
        ("Erste Zeile\r\nZweite Zeile", "Erste Zeile Zweite Zeile"),
        ("Absatz.\n\n  Naechster Absatz.", "Absatz. Naechster Absatz."),
        ("\n  schon einzeilig  \n", "schon einzeilig"),
        ("nichts zu tun", "nichts zu tun"),
    ],
)
def test_single_line_folds_breaks(value, expected):
    assert single_line(value) == expected


def test_address_replacement_stays_on_one_line(detector):
    # Random per call, so the shape is what is asserted, not the value.
    for _ in range(25):
        assert re.fullmatch(r".+, .+", detector.create_replacement_text("ADDRESS"))


@pytest.mark.parametrize("pii_type", sorted(PII_TYPE_MAPPINGS))
def test_no_replacement_carries_a_line_break(detector, pii_type):
    replacement = detector.create_replacement_text(pii_type)
    assert "\n" not in replacement
    assert "\r" not in replacement
    assert replacement == replacement.strip()
