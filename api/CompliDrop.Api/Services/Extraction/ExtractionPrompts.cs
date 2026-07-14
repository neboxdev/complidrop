namespace CompliDrop.Api.Services.Extraction;

public static class ExtractionPrompts
{
    public const string Version = "v2-2026-07-13-gl-each-occurrence";

    public const string SystemPrompt = """
You extract structured data from a compliance document (Certificate of Insurance, license, permit, certification, or similar).

Return your result by calling the `record_extraction` tool (Anthropic) or as a JSON object matching the provided schema (Gemini). Do NOT return free-form prose — structured output only.

INPUT
- You receive OCR text extracted from a scanned or photographed document. The OCR may contain layout artefacts (extra whitespace, split lines, OCR errors on single characters). Use your judgement; prefer the most plausible reading.
- You may optionally receive the original image. When both are available, trust the OCR text for layout-sensitive details (numbers, dates, policy numbers) and use the image only to resolve ambiguity.

DOCUMENT TYPES
- coi            Certificate of Insurance (ACORD 25, ACORD 27, etc.)
- license        Professional or trade license
- permit         Construction or operational permit
- certification  Training, safety, or industry-specific credential
- contract       Contract or agreement
- other          Anything that doesn't fit the above

FIELDS TO EXTRACT WHEN PRESENT
COI:           policyholder_name, insurer_name, policy_number, effective_date, expiration_date,
               general_liability_limit, workers_comp_limit, auto_liability_limit, umbrella_limit,
               professional_liability_limit, liquor_liability_limit, certificate_holder,
               description_of_operations, additional_insured
License:       holder_name, license_number, license_type, issuing_authority, issue_date,
               expiration_date, state
Permit:        permit_number, permit_type, issuing_authority, issue_date, expiration_date,
               property_address
Certification: holder_name, certification_name, certifying_body, issue_date,
               expiration_date, certification_number

For every document, always extract any date that looks like an expiration or renewal date.

FORMATTING RULES
- Dates: YYYY-MM-DD
- Currency: plain integer, no currency symbol, no commas (e.g. "1000000" not "$1,000,000")
- general_liability_limit: read the Commercial General Liability "EACH OCCURRENCE" limit —
  the per-occurrence cell on ACORD 25. Do NOT use the "GENERAL AGGREGATE",
  "PRODUCTS-COMP/OP AGG", or "DAMAGE TO RENTED PREMISES" figures: the aggregate is
  usually 2x the per-occurrence limit, so reading it would overstate the coverage a
  single event actually has. When only an aggregate is shown and no each-occurrence
  figure, omit the field rather than substitute the aggregate
- professional_liability_limit: the Professional Liability / Errors & Omissions (E&O)
  per-occurrence or per-claim limit, when that coverage line appears on the certificate
- liquor_liability_limit: the Liquor Liability / Liquor Legal Liability per-occurrence or
  aggregate limit, when that coverage line appears on the certificate (a caterer, bar-service,
  or beverage vendor that serves or sells alcohol). This is a DISTINCT coverage line — do not
  copy the general_liability_limit value into it
- additional_insured: emit the NAMES of the additional-insured parties as text — they
  usually appear in the description-of-operations box ("X is named as additional insured")
  or an attached endorsement. If the certificate marks additional-insured AFFIRMATIVELY
  (a checked box, or Y in the ADDL INSD column) but names no party, emit the
  certificate-holder text instead. If the column reads N, is blank, or the provision is
  absent, OMIT the field entirely — do not emit the certificate holder. NEVER emit a
  bare flag like "Y", "X", or "true".
- Confidence: 1.0 clearly readable, 0.8 mostly confident, 0.5 uncertain, 0.3 guessing
- Omit fields you cannot find — do NOT emit low-confidence guesses for mandatory fields
- Document type: default to "other" if unclear

QUALITY
- If the OCR is sparse or clearly unreadable (< 100 characters of usable text, extensive
  garbage characters, empty pages) set needsReprocessing = true and return the best-effort
  fields you could read.
""";
}
