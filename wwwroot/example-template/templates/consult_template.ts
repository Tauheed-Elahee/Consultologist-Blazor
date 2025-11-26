export const CONSULT_RESPONSE_TEMPLATE = `{%- comment -%}
Mortigen Oncology Note — HTML output (no fallbacks; pronouns required)

Render context must conform to mortigen_render_context.schema.json
{%- endcomment -%}

{%- assign pf    = front_matter.patient -%}
{%- assign enc   = front_matter.encounter -%}
{%- assign stage = front_matter.staging -%}
{%- assign path  = front_matter.pathology -%}
{%- assign rcpt  = front_matter.receptors -%}
{%- assign onc   = front_matter.oncotype -%}
{%- assign plan  = front_matter.plan_structured -%}
{%- assign side  = extras.side | default: 'right' -%}

{%- assign p_nom  = pf.pronoun.nom -%}
{%- assign p_gen  = pf.pronoun.gen -%}
{%- assign p_obj  = pf.pronoun.obj -%}
{%- assign p_refl = pf.pronoun.refl -%}

{%- assign her2_label  = rcpt.her2.label -%}
{%- assign her2_detail = rcpt.her2.detail -%}

{%- assign sex_label   = pf.sex -%}
{%- assign stage_clean = stage.stage_group | replace: 'Stage ', '' -%}

{%- assign reason_text = content.reason | default: front_matter.diagnosis | join: ', ' -%}

{%- assign meds_items = front_matter.medications -%}
{%- assign allergies_items = front_matter.allergies -%}

{%- comment %} -------------------- Demographics -------------------- {% endcomment -%}
{%- if pf.full_name or pf.name or pf.dob or pf.age_years or pf.sex -%}
  {%- capture demo_line -%}
    {%- if pf.full_name -%}{{ pf.full_name }}{%- elsif pf.name -%}{{ pf.name }}{%- endif -%}
    {%- if pf.dob -%}{% if pf.full_name or pf.name %} — {% endif %}DOB: {{ pf.dob }}{%- endif -%}
    {%- if pf.age_years -%}{% if pf.dob or pf.full_name or pf.name %} — {% endif %}Age: {{ pf.age_years }}{%- endif -%}
    {%- if pf.sex -%}{% if pf.age_years or pf.dob or pf.full_name or pf.name %} — {% endif %}Sex: {{ pf.sex }}{%- endif -%}
  {%- endcapture -%}
  <p>{{ demo_line | strip }}</p>
  <br>
{%- endif -%}

{%- comment %} -------------------- Date/Time -------------------- {% endcomment -%}
{%- if enc.datetime -%}
  <p>{{ enc.datetime | date: "%B %-d, %Y %H:%M %Z" }}</p>
  <br>
{%- endif -%}

{%- comment %} -------------------- Reason -------------------- {% endcomment -%}
{%- if reason_text -%}
  <h2>Reason for Consultation</h2>
  <p>{{ reason_text | strip | newline_to_br }}</p>
  <br>
{%- endif -%}

{%- comment %} -------------------- HPI -------------------- {% endcomment -%}
{%- if content.hpi -%}
  <h2>History of Present Illness</h2>
  <p>{{ content.hpi | strip | newline_to_br }}</p>
  <br>
{%- endif -%}

{%- comment %} -------------------- PMH -------------------- {% endcomment -%}
{%- if content.pmh -%}
  <h2>Past Medical History</h2>
  <p>{{ content.pmh | strip | newline_to_br }}</p>
  <br>
{%- endif -%}

{%- comment %} -------------------- Medication -------------------- {% endcomment -%}
{%- assign meds_block = '' -%}
{%- if meds_items and meds_items.size > 0 -%}
  {%- capture meds_block -%}
    <ul>
    {%- for m in meds_items -%}
      <li>
        {%- if m.label -%}{{ m.label }}{%- else -%}{{ m }}{%- endif -%}
        {%- if m.dose %} {{ m.dose }}{%- if m.dose_unit %} {{ m.dose_unit }}{%- endif -%}{%- endif -%}
        {%- if m.route %} {{ m.route }}{%- endif -%}
        {%- if m.frequency %} {{ m.frequency }}{%- endif -%}
        {%- if m.prn %} PRN{%- endif -%}
        {%- if m.indication %} — for {{ m.indication }}{%- endif -%}
      </li>
    {%- endfor -%}
    </ul>
  {%- endcapture -%}
{%- elsif content.meds -%}
  {%- capture meds_block -%}
    <p>{{ content.meds | strip | newline_to_br }}</p>
  {%- endcapture -%}
{%- endif -%}
{%- if meds_block -%}
  <h2>Medication</h2>
  {{ meds_block }}
  <br>
{%- endif -%}

{%- comment %} -------------------- Allergies -------------------- {% endcomment -%}
{%- assign allergies_block = '' -%}
{%- if allergies_items and allergies_items.size > 0 -%}
  {%- capture allergies_block -%}
    <ul>
    {%- for a in allergies_items -%}
      <li>
        {%- if a.agent -%}{{ a.agent }}{%- else -%}{{ a }}{%- endif -%}
        {%- if a.reaction %} — {{ a.reaction }}{%- endif -%}
        {%- if a.severity %} ({{ a.severity }}){%- endif -%}
      </li>
    {%- endfor -%}
    </ul>
  {%- endcapture -%}
{%- elsif content.allergies -%}
  {%- capture allergies_block -%}
    <p>{{ content.allergies | strip | newline_to_br }}</p>
  {%- endcapture -%}
{%- endif -%}
{%- if allergies_block -%}
  <h2>Allergies</h2>
  {{ allergies_block }}
  <br>
{%- endif -%}

{%- comment %} -------------------- Social -------------------- {% endcomment -%}
{%- if content.social -%}
  <h2>Social History</h2>
  <p>{{ content.social | strip | newline_to_br }}</p>
  <br>
{%- endif -%}

{%- comment %} -------------------- Family -------------------- {% endcomment -%}
{%- if content.family -%}
  <h2>Family History</h2>
  <p>{{ content.family | strip | newline_to_br }}</p>
  <br>
{%- endif -%}

{%- comment %} -------------------- Physical Exam -------------------- {% endcomment -%}
{%- if content.exam or flags.exam_present -%}
  <h2>Physical Exam</h2>
  {%- if content.exam -%}
    <p>{{ content.exam | strip | newline_to_br }}</p>
    <p>
      Alert, attentive, and in no distress. There is no evidence of peripheral lymphadenopathy.
      Cardiovascular and respiratory exams are unremarkable. No tenderness on palpation of the spine.
      Abdominal is soft and nontender, with no palpable masses. No hepatosplenomegaly. No peripheral edema.
      Bilateral breast exam was unremarkable with no worrisome masses or skin changes.
    </p>
  {%- else -%}
    <p>
      Alert, attentive, and in no distress. There is no evidence of peripheral lymphadenopathy.
      Cardiovascular and respiratory exams are unremarkable. No tenderness on palpation of the spine.
      Abdominal is soft and nontender, with no palpable masses. No hepatosplenomegaly. No peripheral edema.
      Bilateral breast exam was unremarkable with no worrisome masses or skin changes.
    </p>
  {%- endif -%}
  <br>
{%- endif -%}

{%- comment %} -------------------- Investigations -------------------- {% endcomment -%}
{%- if content.investigations -%}
  <h2>Investigations</h2>
  <p>{{ content.investigations | strip | newline_to_br }}</p>
  <br>
{%- endif -%}

{%- comment %} -------------------- Assessment / Plan -------------------- {% endcomment -%}
{%- if stage.tnm.T and stage.tnm.N and stage.tnm.M and path.grade and path.histology and rcpt.ER and rcpt.PR -%}
  <h2>Assessment/Plan</h2>

  <p>
    {{ pf.name }} is a {{ pf.age_years }}-year old {{ sex_label }} with a
    {{ stage.tnm.T }}{% if path.tumor_size_cm %} ({{ path.tumor_size_cm }} cm){% endif %} {{ stage.tnm.N }}
    {%- if path.nodes_positive or path.nodes_examined -%}
      ({{ path.nodes_positive }}/{{ path.nodes_examined }})
    {%- endif -%}
    {{ stage.tnm.M }}, grade {{ path.grade }}, ER+ ({{ rcpt.ER }}) PR+ ({{ rcpt.PR }}) {{ her2_label }}
    {%- if her2_detail %} ({{ her2_detail }}){%- endif -%},
    {{ path.histology }} of the {{ side }} breast. Overall, {{ pf.name }} has a stage {{ stage_clean }} breast cancer.
  </p>

  {%- if onc.score -%}
    <p>
      An OncotypeDx test was performed and {{ p_gen }} recurrence score is {{ onc.score }}
      which is associated with a {{ onc.risk_yr }}-year distant recurrence risk of {{ onc.risk_percent }}%
      {%- if onc.absolute_benefit_percent %}. The absolute chemotherapy benefit is estimated to be {{ onc.absolute_benefit_percent }}%{%- endif -%}.
    </p>
  {%- endif -%}

  <p>
    Since {{ pf.name }}'s breast cancer is estrogen receptor positive, hormone therapy is strongly recommended.
    I have recommended 5 years of
    {%- if plan.endocrine and plan.endocrine.agent == 'letrozole' -%}
      letrozole. I discussed the side effects which include, but are not limited to, myalgias, arthralgias, hot flashes,
      fatigue, and a risk of osteopenia and osteoporosis over time. 5 years of letrozole would be expected to reduce
      {{ p_gen }} relative risk of recurrence by 50%.
    {%- elsif plan.endocrine and plan.endocrine.agent == 'tamoxifen' -%}
      tamoxifen. I discussed the side effects which include, but are not limited to, hot flashes, myalgias, arthralgias,
      fatigue, and an under 1% risk of endometrial cancer and VTE. {{ p_nom | capitalize }} understands the teratogenic
      risks of tamoxifen. 5 years of tamoxifen would be expected to reduce {{ p_gen }} relative risk of recurrence by 50%.
    {%- else -%}
      hormonal therapy.
    {%- endif -%}
  </p>

  {%- if plan.radiation_referred -%}
    <p>I will also refer {{ p_obj }} to see one of my radiation oncology colleagues to discuss adjuvant radiation.</p>
  {%- endif -%}

  <p>Finally, we discussed chemotherapy.</p>

  {%- if plan.chemotherapy and plan.chemotherapy.recommended == false and plan.chemotherapy.risk_basis == 'low_oncotype' -%}
    <p>
      Given the low risk features of this patient's breast cancer, I have not recommended adjuvant chemotherapy since any
      small potential benefit would likely be outweighed by the potential disadvantages and side effects.
    </p>
    <p>
      I will get {{ p_obj }} started on adjuvant
      {%- if plan.endocrine and plan.endocrine.agent == 'letrozole' -%} letrozole
      {%- elsif plan.endocrine and plan.endocrine.agent == 'tamoxifen' -%} tamoxifen
      {%- else -%} hormonal therapy
      {%- endif -%} soon.
      I will order a baseline bone mineral density test. I will assess {{ p_obj }} again in 3 months. If all is well at that
      time, I will likely discharge {{ p_obj }} back to the care of {{ p_gen }} family doctor for ongoing surveillance.
    </p>

  {%- elsif plan.chemotherapy and plan.chemotherapy.recommended and plan.chemotherapy.risk_basis == 'her2_positive' -%}
    <p>
      Given {{ p_gen }} {{ her2_label | downcase }} breast cancer, I have recommended trastuzumab-based adjuvant chemotherapy
      due to the significant expected benefit. Potential side effects include, but are not limited to, myelosuppression,
      fatigue, nausea, diarrhea, peripheral neuropathy, and a small risk of cardiotoxicity. Cardiac monitoring with baseline
      and periodic echocardiography will be arranged.
    </p>
    {%- if plan.chemotherapy.regimen == 'AC_TH' -%}
      <p>
        The recommended regimen is AC→TH, with doxorubicin and cyclophosphamide followed by paclitaxel with trastuzumab,
        and trastuzumab continued to complete one year of anti-HER2 therapy.
      </p>
    {%- elsif plan.chemotherapy.regimen == 'TCH' -%}
      <p>
        The recommended regimen is TCH, with docetaxel, carboplatin, and trastuzumab, and trastuzumab continued to complete
        one year of anti-HER2 therapy.
      </p>
    {%- endif -%}

  {%- elsif plan.chemotherapy and plan.chemotherapy.recommended and plan.chemotherapy.regimen == 'BRAJACTG' -%}
    <p>
      Given {{ p_gen }} high risk breast cancer, I have recommended the chemotherapy regimen BRAJACTG which is expected to
      decrease {{ p_gen }} relative risk of recurrence by 1/3. This is an 8 cycle regimen with doxorubicin and cyclophosphamide
      given every 2 weeks for 4 cycles, followed by paclitaxel given every 2 weeks for another 4 cycles. The side effects include,
      but are not limited to, myelosuppression and a risk of febrile neutropenia, chemotherapy associated alopecia, fatigue,
      nausea, vomiting, diarrhea, mucositis, peripheral neuropathy, myalgias and arthralgias, hypersensitivity reactions,
      and a small risk of cardiotoxicity. I have recommended G-CSF support given the myelosuppressive nature of this chemotherapy
      and have arranged this today. I have also given {{ p_obj }} a prescription for antinausea medications. {{ p_nom | capitalize }}
      has consented to starting chemotherapy and I will arrange for {{ p_gen }} first cycle to start in the next few weeks.
      I will see {{ p_obj }} prior to cycle #2.
    </p>

  {%- elsif plan.chemotherapy and plan.chemotherapy.recommended and plan.chemotherapy.regimen == 'BRAJDC' -%}
    <p>
      Given {{ p_gen }} high Oncotype Dx recurrence score, I have recommended the chemotherapy regimen BRAJDC which is expected
      to decrease {{ p_gen }} relative risk of recurrence by 1/3. This is a 4 cycle regimen with docetaxel and cyclophosphamide
      given every 3 weeks. The side effects include, but are not limited to, myelosuppression and a risk of febrile neutropenia,
      chemotherapy associated alopecia, fatigue, nausea, vomiting, diarrhea, mucositis, peripheral neuropathy, myalgias and
      arthralgias. I have recommended G-CSF support given the myelosuppressive nature of this chemotherapy and have arranged this
      today. I have also given {{ p_obj }} a prescription for antinausea medications. {{ p_nom | capitalize }} has consented to
      starting chemotherapy and I will arrange for {{ p_gen }} first cycle to start in the next few weeks. I will see {{ p_obj }}
      prior to cycle #2.
    </p>
  {%- endif -%}

  <p>Thank you, it has been a pleasure to be involved in the care of {{ pf.name }}.</p>
  <br>
{%- endif -%}`;
