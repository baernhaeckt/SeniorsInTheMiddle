import json
import statistics
from dataclasses import asdict

from presidio_analyzer.nlp_engine import NlpEngineProvider

from services.pii_service.models.analyze_result import AnalyzeResult
from services.pii_service.models.detection_result import DetectionResultItem, DetectionResult
from services.pii_service.pii_types import PiiTypes
from services.pii_service.utils.pii_risk_mappings import get_pii_risk_mapping


class PiiDetector:
    DEFAULT_LANGUAGE_CODE = "de"
    SUPPORTED_LANGUAGES = ["de", "en"]

    def __init__(self, language_code: str = DEFAULT_LANGUAGE_CODE):
        """
        Initializes the PiiDetector class.
        """
        try:
            import spacy
            from presidio_analyzer import AnalyzerEngine

            spacy_package_name = "de_core_news_lg" if language_code == "de" else "en_core_web_lg"

            if not spacy.util.is_package(spacy_package_name):
                # Install the model if it is not already installed
                spacy.cli.download(spacy_package_name)

            # Initialize the AnalyzerEngine and configure it for German
            configuration = {
                "nlp_engine_name": "spacy",
                "models": [
                    {"lang_code": "de", "model_name": "de_core_news_lg"},
                    {"lang_code": "en", "model_name": "en_core_web_lg"}
                ],
            }
            provider = NlpEngineProvider(nlp_configuration=configuration)
            self.analyzer = AnalyzerEngine(nlp_engine=provider.create_engine(), supported_languages=self.SUPPORTED_LANGUAGES)

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

    def analyze_text(self, text: str, detection_entities: list[str], language_code: str = DEFAULT_LANGUAGE_CODE) -> str:
        """
        Analyzes the given text for PII entities.
        Args:
            text (str): The text to analyze.
            detection_entities (list[str]): List of PII entity types to detect.
            language_code (str): The language code for the analysis. Defaults to 'en'.
        Returns:
            DetectionResult: The result of the PII detection.
        """
        try:
            detections = self.analyzer.analyze(
                text=text, entities=detection_entities, language=language_code
            )

            detection_entities = list()
            for detection in detections:
                risk_assessment = get_pii_risk_mapping(detection.entity_type)

                detection_entities.append(
                    DetectionResultItem(
                        information_type=risk_assessment.information_type,
                        entity_type=PiiTypes(detection.entity_type).name,
                        score=detection.score,
                        start_position=detection.start,
                        end_position=detection.end,
                        risk_level=risk_assessment.risk_level.value,
                        hipaa_category=risk_assessment.hipaa_category.value
                    )
                )

            pii_analysis_result = self._analyze_detection_result(DetectionResult(detection_results=detection_entities))
            return json.dumps(asdict(pii_analysis_result))  # Return the analysis result as a JSON string
        except Exception as e:
                raise Exception(f"Error analyzing text: {e}")
