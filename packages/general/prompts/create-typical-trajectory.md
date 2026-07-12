Build a typical clinical trajectory for the disease/problem concept.

Output only SNOMED concept bullets in these exact forms:
- term (type) - id number
- term [not SNOMED concept]
- term (type) - id number [not active SNOMED concept]

Include a concise support phrase after the accepted bullet only when needed by appending " -- support: ...".
Do not include commentary, headings, JSON, or non-bullet lines.

Disease/problem concept:
{{ problem_concepts }}
