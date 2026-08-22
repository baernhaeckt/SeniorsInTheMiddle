from services.pii_service.pii_detector import PiiDetector


class AnalyzeService:
    def __init__(self):
        """
        Initializes the AnalyzeService class.
        """
        self.pii_detector = PiiDetector()

