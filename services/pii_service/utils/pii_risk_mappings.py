from enum import Enum
from typing import NamedTuple

from services.pii_service.pii_types import PiiTypes


class RiskLevel(Enum):
    """
    Numerical values assigned to the levels on the continuum presented by Schwartz and Solove (2011)
    """

    LEVEL_ONE = 1  # Not-Identifiable
    LEVEL_TWO = 2  # Semi-Identifiable
    LEVEL_THREE = 3  # Identifiable

class HipaaCategory(Enum):
    """
    Information Categories presented by HIPAA guidelines
    """
    NON_PHI = "Not Protected Health Information"
    PHI = "Protected Health Information"

class PiiMapping(NamedTuple):
    """
    Represents a mapping of a PII type to its associated risk level and provider-specific enum references.
    """
    information_type: str
    risk_level: RiskLevel
    hipaa_category: HipaaCategory
    pii_enum: PiiTypes

PII_TYPE_MAPPINGS: dict[str, PiiMapping] = {
    "DATE": PiiMapping(
        information_type="Birth Date",
        hipaa_category=HipaaCategory.PHI,
        risk_level=RiskLevel.LEVEL_TWO,
        pii_enum=PiiTypes.DATE,
    ),
    "NRP": PiiMapping(
        information_type="Nationality, Religion, Political Affiliation",
        hipaa_category=HipaaCategory.NON_PHI,
        risk_level=RiskLevel.LEVEL_TWO,
        pii_enum=PiiTypes.NRP,
    ),
    "EMAIL_ADDRESS": PiiMapping(
        information_type="Email Address",
        hipaa_category=HipaaCategory.PHI,
        risk_level=RiskLevel.LEVEL_THREE,
        pii_enum=PiiTypes.EMAIL_ADDRESS,
    ),
    "IP_ADDRESS": PiiMapping(
        information_type="IP Address",
        hipaa_category=HipaaCategory.PHI,
        risk_level=RiskLevel.LEVEL_THREE,
        pii_enum=PiiTypes.IP_ADDRESS,
    ),
    "PHONE_NUMBER": PiiMapping(
        information_type="Home Phone Number, Cell Phone Number",
        hipaa_category=HipaaCategory.PHI,
        risk_level=RiskLevel.LEVEL_THREE,
        pii_enum=PiiTypes.PHONE_NUMBER,
    ),
    "ADDRESS": PiiMapping(
        information_type="Address",
        hipaa_category=HipaaCategory.NON_PHI,
        risk_level=RiskLevel.LEVEL_TWO,
        pii_enum=PiiTypes.ADDRESS,
    ),
    "US_DRIVERS_LICENSE_NUMBER": PiiMapping(
        information_type="Driver's License Number",
        hipaa_category=HipaaCategory.PHI,
        risk_level=RiskLevel.LEVEL_THREE,
        pii_enum=PiiTypes.US_DRIVERS_LICENSE_NUMBER,
    ),
    "CREDIT_CARD_NUMBER": PiiMapping(
        information_type="Credit Card Number",
        hipaa_category=HipaaCategory.PHI,
        risk_level=RiskLevel.LEVEL_THREE,
        pii_enum=PiiTypes.CREDIT_CARD_NUMBER,
    ),
    "ABA_ROUTING_NUMBER": PiiMapping(
        information_type="American Bankers Association Routing Number (Financial Accounts)",
        hipaa_category=HipaaCategory.PHI,
        risk_level=RiskLevel.LEVEL_THREE,
        pii_enum=PiiTypes.ABA_ROUTING_NUMBER,
    ),
    "INTERNATIONAL_BANKING_ACCOUNT_NUMBER": PiiMapping(
        information_type="International Banking Account Number (Financial Accounts)",
        hipaa_category=HipaaCategory.PHI,
        risk_level=RiskLevel.LEVEL_THREE,
        pii_enum=PiiTypes.INTERNATIONAL_BANKING_ACCOUNT_NUMBER,
    ),
    "US_BANK_ACCOUNT_NUMBER": PiiMapping(
        information_type="United States Bank Account Number (Financial Accounts)",
        hipaa_category=HipaaCategory.PHI,
        risk_level=RiskLevel.LEVEL_THREE,
        pii_enum=PiiTypes.US_BANK_ACCOUNT_NUMBER,
    ),
    "US_SOCIAL_SECURITY_NUMBER": PiiMapping(
        information_type="Social Security Number",
        hipaa_category=HipaaCategory.PHI,
        risk_level=RiskLevel.LEVEL_THREE,
        pii_enum=PiiTypes.US_SOCIAL_SECURITY_NUMBER,
    ),
    "LOCATION": PiiMapping(
        information_type="GPS Location",
        hipaa_category=HipaaCategory.PHI,
        risk_level=RiskLevel.LEVEL_TWO,
        pii_enum=PiiTypes.LOCATION,
    ),
    "US_PASSPORT_NUMBER": PiiMapping(
        information_type="Passport Number",
        hipaa_category=HipaaCategory.PHI,
        risk_level=RiskLevel.LEVEL_THREE,
        pii_enum=PiiTypes.US_PASSPORT_NUMBER,
    ),
    "AGE": PiiMapping(
        information_type="Age",
        hipaa_category=HipaaCategory.NON_PHI,
        risk_level=RiskLevel.LEVEL_TWO,
        pii_enum=PiiTypes.AGE,
    ),
    "PERSON": PiiMapping(
        information_type="Person",
        hipaa_category=HipaaCategory.PHI,
        risk_level=RiskLevel.LEVEL_THREE,
        pii_enum=PiiTypes.PERSON,
    ),
    "CRYPTO": PiiMapping(
        information_type="Crypto (Financial Accounts)",
        hipaa_category=HipaaCategory.NON_PHI,
        risk_level=RiskLevel.LEVEL_TWO,
        pii_enum=PiiTypes.CRYPTO,
    ),
    "URL": PiiMapping(
        information_type="URL",
        hipaa_category=HipaaCategory.NON_PHI,
        risk_level=RiskLevel.LEVEL_TWO,
        pii_enum=PiiTypes.URL,
    ),
    "DATE_TIME": PiiMapping(
        information_type="Date",
        hipaa_category=HipaaCategory.PHI,
        risk_level=RiskLevel.LEVEL_TWO,
        pii_enum=PiiTypes.DATE_TIME,
    ),
    "MEDICAL_LICENSE": PiiMapping(
        information_type="Medical License",
        hipaa_category=HipaaCategory.NON_PHI,
        risk_level=RiskLevel.LEVEL_THREE,
        pii_enum=PiiTypes.MEDICAL_LICENSE,
    ),
    "US_INDIVIDUAL_TAXPAYER_IDENTIFICATION": PiiMapping(
        information_type="United States Individual Taxpayer Identification",
        hipaa_category=HipaaCategory.PHI,
        risk_level=RiskLevel.LEVEL_THREE,
        pii_enum=PiiTypes.US_INDIVIDUAL_TAXPAYER_IDENTIFICATION,
    ),
    "AU_BUSINESS_NUMBER": PiiMapping(
        information_type="Australian Business Number",
        hipaa_category=HipaaCategory.PHI,
        risk_level=RiskLevel.LEVEL_THREE,
        pii_enum=PiiTypes.AU_BUSINESS_NUMBER,
    ),
    "AU_COMPANY_NUMBER": PiiMapping(
        information_type="Australian Company Number",
        hipaa_category=HipaaCategory.PHI,
        risk_level=RiskLevel.LEVEL_THREE,
        pii_enum=PiiTypes.AU_COMPANY_NUMBER,
    ),
    "AU_MEDICAL_ACCOUNT_NUMBER": PiiMapping(
        information_type="Australian Medicare Number",
        hipaa_category=HipaaCategory.PHI,
        risk_level=RiskLevel.LEVEL_THREE,
        pii_enum=PiiTypes.AU_MEDICAL_ACCOUNT_NUMBER,
    ),
    "AU_TAX_FILE_NUMBER": PiiMapping(
        information_type="Australian Tax File Number",
        hipaa_category=HipaaCategory.PHI,
        risk_level=RiskLevel.LEVEL_THREE,
        pii_enum=PiiTypes.AU_TAX_FILE_NUMBER,
    ),
    # Presidio extended (global and regional)
    "MAC_ADDRESS": PiiMapping(
        information_type="MAC Address",
        hipaa_category=HipaaCategory.NON_PHI,
        risk_level=RiskLevel.LEVEL_TWO,
        pii_enum=PiiTypes.MAC_ADDRESS,
    ),
    "US_MBI": PiiMapping(
        information_type="US Medicare Beneficiary Identifier",
        hipaa_category=HipaaCategory.PHI,
        risk_level=RiskLevel.LEVEL_THREE,
        pii_enum=PiiTypes.US_MBI,
    ),
    "UK_NHS": PiiMapping(
        information_type="UK National Health Service Number",
        hipaa_category=HipaaCategory.PHI,
        risk_level=RiskLevel.LEVEL_THREE,
        pii_enum=PiiTypes.UK_NHS,
    ),
    "UK_NINO": PiiMapping(
        information_type="UK National Insurance Number",
        hipaa_category=HipaaCategory.PHI,
        risk_level=RiskLevel.LEVEL_THREE,
        pii_enum=PiiTypes.UK_NINO,
    ),
    "ES_NIF": PiiMapping(
        information_type="Spain NIF (Tax ID)",
        hipaa_category=HipaaCategory.PHI,
        risk_level=RiskLevel.LEVEL_THREE,
        pii_enum=PiiTypes.ES_NIF,
    ),
    "ES_NIE": PiiMapping(
        information_type="Spain NIE (Foreigner ID)",
        hipaa_category=HipaaCategory.PHI,
        risk_level=RiskLevel.LEVEL_THREE,
        pii_enum=PiiTypes.ES_NIE,
    ),
    "IT_FISCAL_CODE": PiiMapping(
        information_type="Italy Fiscal Code (Codice Fiscale)",
        hipaa_category=HipaaCategory.PHI,
        risk_level=RiskLevel.LEVEL_THREE,
        pii_enum=PiiTypes.IT_FISCAL_CODE,
    ),
    "IT_DRIVER_LICENSE": PiiMapping(
        information_type="Italy Driver License",
        hipaa_category=HipaaCategory.PHI,
        risk_level=RiskLevel.LEVEL_THREE,
        pii_enum=PiiTypes.IT_DRIVER_LICENSE,
    ),
    "IT_VAT_CODE": PiiMapping(
        information_type="Italy VAT Code",
        hipaa_category=HipaaCategory.NON_PHI,
        risk_level=RiskLevel.LEVEL_TWO,
        pii_enum=PiiTypes.IT_VAT_CODE,
    ),
    "IT_PASSPORT": PiiMapping(
        information_type="Italy Passport Number",
        hipaa_category=HipaaCategory.PHI,
        risk_level=RiskLevel.LEVEL_THREE,
        pii_enum=PiiTypes.IT_PASSPORT,
    ),
    "IT_IDENTITY_CARD": PiiMapping(
        information_type="Italy Identity Card",
        hipaa_category=HipaaCategory.PHI,
        risk_level=RiskLevel.LEVEL_THREE,
        pii_enum=PiiTypes.IT_IDENTITY_CARD,
    ),
    "PL_PESEL": PiiMapping(
        information_type="Poland PESEL (National ID)",
        hipaa_category=HipaaCategory.PHI,
        risk_level=RiskLevel.LEVEL_THREE,
        pii_enum=PiiTypes.PL_PESEL,
    ),
    "SG_NRIC_FIN": PiiMapping(
        information_type="Singapore NRIC/FIN",
        hipaa_category=HipaaCategory.PHI,
        risk_level=RiskLevel.LEVEL_THREE,
        pii_enum=PiiTypes.SG_NRIC_FIN,
    ),
    "SG_UEN": PiiMapping(
        information_type="Singapore UEN (Business ID)",
        hipaa_category=HipaaCategory.NON_PHI,
        risk_level=RiskLevel.LEVEL_TWO,
        pii_enum=PiiTypes.SG_UEN,
    ),
    "IN_PAN": PiiMapping(
        information_type="India PAN (Permanent Account Number)",
        hipaa_category=HipaaCategory.PHI,
        risk_level=RiskLevel.LEVEL_THREE,
        pii_enum=PiiTypes.IN_PAN,
    ),
    "IN_AADHAAR": PiiMapping(
        information_type="India Aadhaar",
        hipaa_category=HipaaCategory.PHI,
        risk_level=RiskLevel.LEVEL_THREE,
        pii_enum=PiiTypes.IN_AADHAAR,
    ),
    "IN_VEHICLE_REGISTRATION": PiiMapping(
        information_type="India Vehicle Registration",
        hipaa_category=HipaaCategory.NON_PHI,
        risk_level=RiskLevel.LEVEL_TWO,
        pii_enum=PiiTypes.IN_VEHICLE_REGISTRATION,
    ),
    "IN_VOTER": PiiMapping(
        information_type="India Voter ID",
        hipaa_category=HipaaCategory.PHI,
        risk_level=RiskLevel.LEVEL_THREE,
        pii_enum=PiiTypes.IN_VOTER,
    ),
    "IN_PASSPORT": PiiMapping(
        information_type="India Passport Number",
        hipaa_category=HipaaCategory.PHI,
        risk_level=RiskLevel.LEVEL_THREE,
        pii_enum=PiiTypes.IN_PASSPORT,
    ),
    "IN_GSTIN": PiiMapping(
        information_type="India GST Identification Number",
        hipaa_category=HipaaCategory.NON_PHI,
        risk_level=RiskLevel.LEVEL_TWO,
        pii_enum=PiiTypes.IN_GSTIN,
    ),
    "FI_PERSONAL_IDENTITY_CODE": PiiMapping(
        information_type="Finland Personal Identity Code",
        hipaa_category=HipaaCategory.PHI,
        risk_level=RiskLevel.LEVEL_THREE,
        pii_enum=PiiTypes.FI_PERSONAL_IDENTITY_CODE,
    ),
    "KR_DRIVER_LICENSE": PiiMapping(
        information_type="Korea Driver License",
        hipaa_category=HipaaCategory.PHI,
        risk_level=RiskLevel.LEVEL_THREE,
        pii_enum=PiiTypes.KR_DRIVER_LICENSE,
    ),
    "KR_FRN": PiiMapping(
        information_type="Korea Foreign Registration Number",
        hipaa_category=HipaaCategory.PHI,
        risk_level=RiskLevel.LEVEL_THREE,
        pii_enum=PiiTypes.KR_FRN,
    ),
    "KR_PASSPORT": PiiMapping(
        information_type="Korea Passport Number",
        hipaa_category=HipaaCategory.PHI,
        risk_level=RiskLevel.LEVEL_THREE,
        pii_enum=PiiTypes.KR_PASSPORT,
    ),
    "KR_BRN": PiiMapping(
        information_type="Korea Business Registration Number",
        hipaa_category=HipaaCategory.NON_PHI,
        risk_level=RiskLevel.LEVEL_TWO,
        pii_enum=PiiTypes.KR_BRN,
    ),
    "KR_RRN": PiiMapping(
        information_type="Korea Resident Registration Number",
        hipaa_category=HipaaCategory.PHI,
        risk_level=RiskLevel.LEVEL_THREE,
        pii_enum=PiiTypes.KR_RRN,
    ),
    "TH_TNIN": PiiMapping(
        information_type="Thailand National ID Number",
        hipaa_category=HipaaCategory.PHI,
        risk_level=RiskLevel.LEVEL_THREE,
        pii_enum=PiiTypes.TH_TNIN,
    ),
}

def get_pii_risk_mapping(pii_type: str) -> PiiMapping:
    """
    Retrieves the PII mapping for a given PII type.
    Args:
        pii_type (str): The PII type to retrieve the mapping for.
    Returns:
        PiiMapping: The mapping for the given PII type.
    """
    if pii_type not in PII_TYPE_MAPPINGS:
        raise ValueError(f"PII type '{pii_type}' is not recognized.")

    return PII_TYPE_MAPPINGS[pii_type]