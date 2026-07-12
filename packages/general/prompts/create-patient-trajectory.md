Reconcile a patient-specific trajectory from the validated patient concepts and typical trajectory.

Output only SNOMED concept bullets in these exact forms:
- term (type) - id number
- term [not SNOMED concept]
- term (type) - id number [not active SNOMED concept]

Include only patient-specific trajectory details supported by validated patient concepts. Do not add typical trajectory details unless supported by patient concepts.
Include a concise support phrase after the accepted bullet only when needed by appending " -- support: ...".
Do not include commentary, headings, JSON, or non-bullet lines.

Disease/problem concept:
{{ problem_concepts }}

Validated patient concepts:
{{ patient_concepts }}

Typical trajectory concepts:
{{ typical_trajectory_concepts }}
