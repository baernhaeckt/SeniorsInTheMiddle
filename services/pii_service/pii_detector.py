import logging
import os
import re
import statistics
from dataclasses import replace

from presidio_analyzer.nlp_engine import NlpEngineProvider

from .config import settings
from .models.analyze_result import AnalyzeResult
from .models.detection_result import DetectionResultItem, DetectionResult, to_dict
from .pii_types import PiiTypes
from .utils.pii_risk_mappings import get_pii_risk_mapping

logger = logging.getLogger(__name__)

# A replacement stands in for a value that sat inside a JSON string, so a line
# break in it is not the shape the original had: it escapes to a literal \n in
# the rewritten body and reads as one in the inspector. The individual fakers
# are written to stay on one line; this is the invariant they are written to.
_LINE_BREAKS = re.compile(r"\s*[\r\n]+\s*")


def single_line(value: str) -> str:
    """Fold a replacement onto one line, as the value it replaces was."""
    return _LINE_BREAKS.sub(" ", value).strip()


class PiiDetector:
    """Finds personal data in text with Presidio and a spaCy language model.

    Loading the model is expensive, so one detector is built at startup and reused for
    every request.
    """

    SUPPORTED_LANGUAGES = ["de", "en"]

    def __init__(self, language_code: str = settings.DEFAULT_LANGUAGE) -> None:
        """
        Initializes the PiiDetector class.
        """
        try:
            import spacy
            from presidio_analyzer import AnalyzerEngine

            spacy_package_name = "de_core_news_lg" if language_code == "de" else "en_core_web_lg"
            logger.info(f"Loading spacy package '{spacy_package_name}'")

            if not spacy.util.is_package(spacy_package_name):
                # In the container the model is baked in at build time (see backend/Dockerfile,
                # SPACY_MODELS). A runtime download needs network access and a writable
                # site-packages, so it is opt-in for local development only.
                if os.environ.get("PII_ALLOW_MODEL_DOWNLOAD", "") == "1":
                    logger.warning(f"spaCy model '{spacy_package_name}' missing, downloading it")
                    spacy.cli.download(spacy_package_name)
                else:
                    raise RuntimeError(
                        f"spaCy model '{spacy_package_name}' is not installed. Run "
                        f"'python -m spacy download {spacy_package_name}' or set PII_ALLOW_MODEL_DOWNLOAD=1."
                    )

            # Initialize the AnalyzerEngine and configure it for German
            configuration = {
                "nlp_engine_name": "spacy",
                "models": [
                    {"lang_code": f"{language_code}", "model_name": f"{spacy_package_name}"},
                ],
            }
            logger.info(f"Loading spacy configuration '{configuration['nlp_engine_name']}'")

            provider = NlpEngineProvider(nlp_configuration=configuration)
            self.analyzer = AnalyzerEngine(nlp_engine=provider.create_engine(), supported_languages=self.SUPPORTED_LANGUAGES)
            self.language_code = language_code

        except ImportError as e:
            raise Exception(f"Missing dependencies. {e}")

    def _analyze_detection_result(self, detection_result: DetectionResult) -> AnalyzeResult:
        """
        Analyzes the detection result to provide additional insights.
        Args:
            detection_result (DetectionResult): The result of the PII detection.
        Returns:
            AnalyzeResult: The analysis of the detection result.
        """
        risk_score_mean = statistics.mean([r.score for r in detection_result.detection_results])
        risk_score_median = statistics.median([r.score for r in detection_result.detection_results])
        detected_pii_types = list(set([r.entity_type for r in detection_result.detection_results]))
        detected_pii_type_frequencies = {entity_type: sum(1 for r in detection_result.detection_results if r.entity_type == entity_type) for entity_type in detected_pii_types}

        return AnalyzeResult(
            detection_results=detection_result.detection_results,
            detection_count=len(detection_result.detection_results),
            risk_score_mean=risk_score_mean,
            risk_score_median=risk_score_median,
            detected_pii_types=detected_pii_types,
            detected_pii_type_frequencies=detected_pii_type_frequencies
        )

    @staticmethod
    def _to_item(text: str, detection) -> DetectionResultItem:
        pii_type_name = PiiTypes(detection.entity_type).name
        pii_mapping = get_pii_risk_mapping(pii_type_name)

        return DetectionResultItem(
            information_type=pii_mapping.information_type,
            entity_type=pii_type_name,
            score=detection.score,
            start_position=detection.start,
            end_position=detection.end,
            detected_text=text[detection.start:detection.end],
            risk_level=pii_mapping.risk_level.value,
            hipaa_category=pii_mapping.hipaa_category.value
        )

    def analyze_text(self, text: str, detection_entities: list[str]) -> dict:
        """
        Analyzes the given text for PII entities.
        Args:
            text (str): The text to analyze.
            detection_entities (list[str]): List of PII entity types to detect.
        Returns:
            DetectionResult: The result of the PII detection.
        """
        try:
            logger.info(f"Analyzing text in language '{self.language_code}' for entities: {detection_entities}")
            detections = self.analyzer.analyze(
                text=text, entities=detection_entities, language=self.language_code
            )
            logger.info(f"Found {len(detections)} potential PII entities.")

            detection_entities = list()
            ignored_entities = list()
            for detection in detections:
                item = self._to_item(text, detection)

                if detection.score < settings.PII_SCORE_THRESHOLD:
                    logger.info(f"Ignored PII: {detection}")
                    ignored_entities.append(item)
                    continue

                logger.info(f"Detected PII: {detection}")
                detection_entities.append(item)

            if len(detection_entities) == 0:
                logger.info("No PII entities found.")
                if len(ignored_entities) == 0:
                    return dict()
                return {"ignored_results": [to_dict(item) for item in ignored_entities]}

            logger.info(f"Starting analysis of detection results for {len(detection_entities)} entities.")
            pii_analysis_result = self._analyze_detection_result(DetectionResult(detection_results=detection_entities))
            pii_analysis_result = replace(pii_analysis_result, ignored_results=ignored_entities)

            return to_dict(pii_analysis_result)
        except Exception as e:
                raise Exception(f"Error analyzing text: {e}")

    def create_replacement_text(self, pii_type: str) -> str:
        """
        Creates a replacement text for the given PII type.
        Args:
            pii_type (str): The PII type for which to create a replacement text.
        Returns:
            str: The replacement text.
        """
        logger.info(f"Creating replacement text for PII type '{pii_type}'.")

        pii_mapping = get_pii_risk_mapping(pii_type)
        return single_line(str(pii_mapping.faker()))