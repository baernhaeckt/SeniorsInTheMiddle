import logging
import statistics

from presidio_analyzer.nlp_engine import NlpEngineProvider

from services.pii_service.config import settings
from services.pii_service.models.analyze_result import AnalyzeResult
from services.pii_service.models.detection_result import DetectionResultItem, DetectionResult, to_dict
from services.pii_service.pii_types import PiiTypes
from services.pii_service.utils.pii_risk_mappings import get_pii_risk_mapping

logger = logging.getLogger(__name__)

class PiiDetector:
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
                # Install the model if it is not already installed
                spacy.cli.download(spacy_package_name)

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
            for detection in detections:
                pii_type_name = PiiTypes(detection.entity_type).name
                pii_mapping = get_pii_risk_mapping(pii_type_name)
                detected_text = text[detection.start:detection.end]

                detection_entities.append(
                    DetectionResultItem(
                        information_type=pii_mapping.information_type,
                        entity_type=pii_type_name,
                        score=detection.score,
                        start_position=detection.start,
                        end_position=detection.end,
                        detected_text=detected_text,
                        replacement_text=pii_mapping.faker,
                        risk_level=pii_mapping.risk_level.value,
                        hipaa_category=pii_mapping.hipaa_category.value
                    )
                )

            if len(detection_entities) == 0:
                logger.info("No PII entities found.")
                return dict()

            logger.info(f"Starting analysis of detection results for {len(detection_entities)} entities.")
            pii_analysis_result = self._analyze_detection_result(DetectionResult(detection_results=detection_entities))

            return to_dict(pii_analysis_result)
        except Exception as e:
                raise Exception(f"Error analyzing text: {e}")
