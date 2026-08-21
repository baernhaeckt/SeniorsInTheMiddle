from services.pii_service.models.detection_result import DetectionResultItem
from services.pii_service.pii_types import PiiTypes


class PiiAnalyzer:
    DEFAULT_LANGUAGE_CODE = "en"

    def __init__(self):
        """
        Initializes the PiiAnalyzer class.
        """
        try:
            import spacy
            from presidio_analyzer import AnalyzerEngine

            if not spacy.util.is_package("en_core_web_lg"):
                # Install the model if it is not already installed
                spacy.cli.download("en_core_web_lg")

            # Initialize the AnalyzerEngine
            self.analyzer = AnalyzerEngine()

        except ImportError:
            raise Exception("Missing dependencies.")

        def analyze_text(text: str, detection_entities: list[str], language_code: str = self.DEFAULT_LANGUAGE_CODE) -> list[DetectionResultItem]:
            """
            Analyzes the given text for PII entities.
            Args:
                text (str): The text to analyze.
                detection_entities (list[str]): List of PII entity types to detect.
                language_code (str): The language code for the analysis. Defaults to 'en'.
            """
            try:
                detections = self.analyzer.analyze(
                    text=text, entities=detection_entities, language=language_code
                )

                detection_entities = list()
                for detection in detections:
                    detection_entities.append(
                        DetectionResultItem(
                            entity_type=PiiTypes(detection.entity_type).name,
                            score=detection.score,
                            start_position=detection.start,
                            end_position=detection.end
                        )
                    )

                return detection_entities
            except Exception as e:
                raise Exception(f"Error analyzing text: {e}")


