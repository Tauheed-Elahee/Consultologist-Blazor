"use strict";
export const validate = validate20;
export default validate20;
const schema31 = {
	$schema: "https://json-schema.org/draft/2020-12/schema",
	$id: "https://consultologist.ai/schemas/mortigen_render_context.schema.json",
	title: "Mortigen Render Context",
	type: "object",
	additionalProperties: false,
	required: ["front_matter"],
	properties: {
		front_matter: {
			type: "object",
			additionalProperties: false,
			required: [
				"patient",
				"staging",
				"pathology",
				"receptors",
				"plan_structured",
			],
			properties: {
				patient: {
					type: "object",
					additionalProperties: false,
					required: ["name", "age_years", "sex", "pronoun"],
					properties: {
						name: { type: "string" },
						full_name: { type: "string" },
						dob: { type: "string" },
						age_years: { type: "integer", minimum: 0, maximum: 130 },
						sex: {
							type: "string",
							enum: ["female", "male", "other", "unknown"],
						},
						pronoun: {
							type: "object",
							additionalProperties: false,
							required: ["nom", "gen", "obj", "refl"],
							properties: {
								nom: { type: "string" },
								gen: { type: "string" },
								obj: { type: "string" },
								refl: { type: "string" },
							},
						},
					},
				},
				encounter: {
					type: "object",
					additionalProperties: false,
					properties: { datetime: { type: "string" } },
				},
				diagnosis: { type: "array", items: { type: "string" } },
				staging: {
					type: "object",
					additionalProperties: false,
					required: ["stage_group", "tnm"],
					properties: {
						stage_group: {
							type: "string",
							pattern: "^Stage\\s*(0|I{1,3}(A|B|C)?|IV)$",
						},
						tnm: {
							type: "object",
							additionalProperties: false,
							required: ["prefix", "T", "N", "M"],
							properties: {
								prefix: { type: "string", enum: ["p", "c", "yp", "yc", "x"] },
								T: {
									type: "string",
									pattern: "^T(is|1a|1b|1c|1|2|3|4[abcd]?)$",
								},
								N: { type: "string", pattern: "^N(0|1[abc]?|2[ab]?|3[abc]?)$" },
								M: { type: "string", pattern: "^M(0|1|X)$" },
							},
						},
					},
				},
				pathology: {
					type: "object",
					additionalProperties: false,
					required: [
						"histology",
						"grade",
						"tumor_size_cm",
						"nodes_examined",
						"nodes_positive",
					],
					properties: {
						histology: { type: "string" },
						grade: { type: "string", pattern: "^[123]$" },
						tumor_size_cm: { type: "number", minimum: 0, maximum: 20 },
						dcis_present: { type: "boolean" },
						margins: { type: "string" },
						nodes_examined: { type: "integer", minimum: 0 },
						nodes_positive: { type: "integer", minimum: 0 },
					},
				},
				receptors: {
					type: "object",
					additionalProperties: false,
					required: ["ER", "PR", "her2"],
					properties: {
						ER: { type: "string", pattern: "^[0-8]/8$" },
						PR: { type: "string", pattern: "^[0-8]/8$" },
						her2: {
							type: "object",
							additionalProperties: false,
							properties: {
								raw_text: { type: "string" },
								label: {
									type: "string",
									enum: [
										"HER2-positive",
										"HER2-negative",
										"HER2 status equivocal/unknown",
									],
								},
								detail: { type: "string" },
								ihc_score: {
									type: ["string", "null"],
									enum: ["0", "1+", "2+", "3+", null],
								},
								fish_result: {
									type: ["string", "null"],
									enum: ["positive", "negative", "equivocal", "not_done", null],
								},
							},
							allOf: [
								{
									if: { required: ["label"] },
									then: { required: ["raw_text"] },
								},
							],
						},
					},
				},
				oncotype: {
					type: "object",
					additionalProperties: false,
					properties: {
						score: { type: "number", minimum: 0, maximum: 100 },
						risk_yr: { type: "integer", minimum: 1, maximum: 100 },
						risk_percent: { type: "number", minimum: 0, maximum: 100 },
						absolute_benefit_percent: {
							type: "number",
							minimum: 0,
							maximum: 100,
						},
					},
					allOf: [
						{
							if: { required: ["score"] },
							then: { required: ["risk_yr", "risk_percent"] },
						},
					],
				},
				plan_structured: {
					type: "object",
					additionalProperties: false,
					properties: {
						endocrine: {
							type: "object",
							additionalProperties: false,
							properties: {
								ordered: { type: "boolean" },
								agent: {
									type: "string",
									enum: ["letrozole", "tamoxifen", "other"],
								},
								agent_other: { type: ["string", "null"] },
							},
						},
						radiation_referred: { type: "boolean" },
						chemotherapy: {
							type: "object",
							additionalProperties: false,
							properties: {
								recommended: { type: "boolean" },
								risk_basis: {
									type: "string",
									enum: [
										"low_oncotype",
										"high_oncotype",
										"high_risk_features",
										"her2_positive",
										"other",
									],
								},
								regimen: {
									type: ["string", "null"],
									enum: ["BRAJACTG", "BRAJDC", "AC_TH", "TCH", null],
								},
								regimen_other: { type: ["string", "null"] },
							},
						},
					},
				},
				medications: {
					type: "array",
					items: {
						type: "object",
						additionalProperties: false,
						properties: {
							label: { type: "string" },
							dose: { type: ["string", "number"] },
							dose_unit: { type: "string" },
							route: { type: "string" },
							frequency: { type: "string" },
							prn: { type: "boolean" },
							indication: { type: ["string", "null"] },
						},
					},
				},
				allergies: {
					type: "array",
					items: {
						type: "object",
						additionalProperties: false,
						properties: {
							agent: { type: "string" },
							reaction: { type: "string" },
							severity: { type: "string" },
						},
					},
				},
			},
		},
		content: {
			type: "object",
			additionalProperties: false,
			properties: {
				reason: { type: ["string", "null"] },
				hpi: { type: ["string", "null"] },
				pmh: { type: ["string", "null"] },
				meds: { type: ["string", "null"] },
				allergies: { type: ["string", "null"] },
				social: { type: ["string", "null"] },
				family: { type: ["string", "null"] },
				exam: { type: ["string", "null"] },
				investigations: { type: ["string", "null"] },
			},
		},
		extras: {
			type: "object",
			additionalProperties: false,
			properties: { side: { type: "string", enum: ["left", "right"] } },
		},
		flags: {
			type: "object",
			additionalProperties: false,
			properties: { exam_present: { type: "boolean" } },
		},
	},
};
const func1 = Object.prototype.hasOwnProperty;
const pattern4 = new RegExp("^Stage\\s*(0|I{1,3}(A|B|C)?|IV)$", "u");
const pattern5 = new RegExp("^T(is|1a|1b|1c|1|2|3|4[abcd]?)$", "u");
const pattern6 = new RegExp("^N(0|1[abc]?|2[ab]?|3[abc]?)$", "u");
const pattern7 = new RegExp("^M(0|1|X)$", "u");
const pattern8 = new RegExp("^[123]$", "u");
const pattern9 = new RegExp("^[0-8]/8$", "u");
function validate20(
	data,
	{
		instancePath = "",
		parentData,
		parentDataProperty,
		rootData = data,
		dynamicAnchors = {},
	} = {},
) {
	/*# sourceURL="https://consultologist.ai/schemas/mortigen_render_context.schema.json" */ let vErrors =
		null;
	let errors = 0;
	const evaluated0 = validate20.evaluated;
	if (evaluated0.dynamicProps) {
		evaluated0.props = undefined;
	}
	if (evaluated0.dynamicItems) {
		evaluated0.items = undefined;
	}
	if (data && typeof data == "object" && !Array.isArray(data)) {
		if (data.front_matter === undefined) {
			const err0 = {
				instancePath,
				schemaPath: "#/required",
				keyword: "required",
				params: { missingProperty: "front_matter" },
				message: "must have required property '" + "front_matter" + "'",
			};
			if (vErrors === null) {
				vErrors = [err0];
			} else {
				vErrors.push(err0);
			}
			errors++;
		}
		for (const key0 in data) {
			if (
				!(
					key0 === "front_matter" ||
					key0 === "content" ||
					key0 === "extras" ||
					key0 === "flags"
				)
			) {
				const err1 = {
					instancePath,
					schemaPath: "#/additionalProperties",
					keyword: "additionalProperties",
					params: { additionalProperty: key0 },
					message: "must NOT have additional properties",
				};
				if (vErrors === null) {
					vErrors = [err1];
				} else {
					vErrors.push(err1);
				}
				errors++;
			}
		}
		if (data.front_matter !== undefined) {
			let data0 = data.front_matter;
			if (data0 && typeof data0 == "object" && !Array.isArray(data0)) {
				if (data0.patient === undefined) {
					const err2 = {
						instancePath: instancePath + "/front_matter",
						schemaPath: "#/properties/front_matter/required",
						keyword: "required",
						params: { missingProperty: "patient" },
						message: "must have required property '" + "patient" + "'",
					};
					if (vErrors === null) {
						vErrors = [err2];
					} else {
						vErrors.push(err2);
					}
					errors++;
				}
				if (data0.staging === undefined) {
					const err3 = {
						instancePath: instancePath + "/front_matter",
						schemaPath: "#/properties/front_matter/required",
						keyword: "required",
						params: { missingProperty: "staging" },
						message: "must have required property '" + "staging" + "'",
					};
					if (vErrors === null) {
						vErrors = [err3];
					} else {
						vErrors.push(err3);
					}
					errors++;
				}
				if (data0.pathology === undefined) {
					const err4 = {
						instancePath: instancePath + "/front_matter",
						schemaPath: "#/properties/front_matter/required",
						keyword: "required",
						params: { missingProperty: "pathology" },
						message: "must have required property '" + "pathology" + "'",
					};
					if (vErrors === null) {
						vErrors = [err4];
					} else {
						vErrors.push(err4);
					}
					errors++;
				}
				if (data0.receptors === undefined) {
					const err5 = {
						instancePath: instancePath + "/front_matter",
						schemaPath: "#/properties/front_matter/required",
						keyword: "required",
						params: { missingProperty: "receptors" },
						message: "must have required property '" + "receptors" + "'",
					};
					if (vErrors === null) {
						vErrors = [err5];
					} else {
						vErrors.push(err5);
					}
					errors++;
				}
				if (data0.plan_structured === undefined) {
					const err6 = {
						instancePath: instancePath + "/front_matter",
						schemaPath: "#/properties/front_matter/required",
						keyword: "required",
						params: { missingProperty: "plan_structured" },
						message: "must have required property '" + "plan_structured" + "'",
					};
					if (vErrors === null) {
						vErrors = [err6];
					} else {
						vErrors.push(err6);
					}
					errors++;
				}
				for (const key1 in data0) {
					if (!func1.call(schema31.properties.front_matter.properties, key1)) {
						const err7 = {
							instancePath: instancePath + "/front_matter",
							schemaPath: "#/properties/front_matter/additionalProperties",
							keyword: "additionalProperties",
							params: { additionalProperty: key1 },
							message: "must NOT have additional properties",
						};
						if (vErrors === null) {
							vErrors = [err7];
						} else {
							vErrors.push(err7);
						}
						errors++;
					}
				}
				if (data0.patient !== undefined) {
					let data1 = data0.patient;
					if (data1 && typeof data1 == "object" && !Array.isArray(data1)) {
						if (data1.name === undefined) {
							const err8 = {
								instancePath: instancePath + "/front_matter/patient",
								schemaPath:
									"#/properties/front_matter/properties/patient/required",
								keyword: "required",
								params: { missingProperty: "name" },
								message: "must have required property '" + "name" + "'",
							};
							if (vErrors === null) {
								vErrors = [err8];
							} else {
								vErrors.push(err8);
							}
							errors++;
						}
						if (data1.age_years === undefined) {
							const err9 = {
								instancePath: instancePath + "/front_matter/patient",
								schemaPath:
									"#/properties/front_matter/properties/patient/required",
								keyword: "required",
								params: { missingProperty: "age_years" },
								message: "must have required property '" + "age_years" + "'",
							};
							if (vErrors === null) {
								vErrors = [err9];
							} else {
								vErrors.push(err9);
							}
							errors++;
						}
						if (data1.sex === undefined) {
							const err10 = {
								instancePath: instancePath + "/front_matter/patient",
								schemaPath:
									"#/properties/front_matter/properties/patient/required",
								keyword: "required",
								params: { missingProperty: "sex" },
								message: "must have required property '" + "sex" + "'",
							};
							if (vErrors === null) {
								vErrors = [err10];
							} else {
								vErrors.push(err10);
							}
							errors++;
						}
						if (data1.pronoun === undefined) {
							const err11 = {
								instancePath: instancePath + "/front_matter/patient",
								schemaPath:
									"#/properties/front_matter/properties/patient/required",
								keyword: "required",
								params: { missingProperty: "pronoun" },
								message: "must have required property '" + "pronoun" + "'",
							};
							if (vErrors === null) {
								vErrors = [err11];
							} else {
								vErrors.push(err11);
							}
							errors++;
						}
						for (const key2 in data1) {
							if (
								!(
									key2 === "name" ||
									key2 === "full_name" ||
									key2 === "dob" ||
									key2 === "age_years" ||
									key2 === "sex" ||
									key2 === "pronoun"
								)
							) {
								const err12 = {
									instancePath: instancePath + "/front_matter/patient",
									schemaPath:
										"#/properties/front_matter/properties/patient/additionalProperties",
									keyword: "additionalProperties",
									params: { additionalProperty: key2 },
									message: "must NOT have additional properties",
								};
								if (vErrors === null) {
									vErrors = [err12];
								} else {
									vErrors.push(err12);
								}
								errors++;
							}
						}
						if (data1.name !== undefined) {
							if (typeof data1.name !== "string") {
								const err13 = {
									instancePath: instancePath + "/front_matter/patient/name",
									schemaPath:
										"#/properties/front_matter/properties/patient/properties/name/type",
									keyword: "type",
									params: { type: "string" },
									message: "must be string",
								};
								if (vErrors === null) {
									vErrors = [err13];
								} else {
									vErrors.push(err13);
								}
								errors++;
							}
						}
						if (data1.full_name !== undefined) {
							if (typeof data1.full_name !== "string") {
								const err14 = {
									instancePath:
										instancePath + "/front_matter/patient/full_name",
									schemaPath:
										"#/properties/front_matter/properties/patient/properties/full_name/type",
									keyword: "type",
									params: { type: "string" },
									message: "must be string",
								};
								if (vErrors === null) {
									vErrors = [err14];
								} else {
									vErrors.push(err14);
								}
								errors++;
							}
						}
						if (data1.dob !== undefined) {
							if (typeof data1.dob !== "string") {
								const err15 = {
									instancePath: instancePath + "/front_matter/patient/dob",
									schemaPath:
										"#/properties/front_matter/properties/patient/properties/dob/type",
									keyword: "type",
									params: { type: "string" },
									message: "must be string",
								};
								if (vErrors === null) {
									vErrors = [err15];
								} else {
									vErrors.push(err15);
								}
								errors++;
							}
						}
						if (data1.age_years !== undefined) {
							let data5 = data1.age_years;
							if (
								!(
									typeof data5 == "number" &&
									!(data5 % 1) &&
									!isNaN(data5) &&
									isFinite(data5)
								)
							) {
								const err16 = {
									instancePath:
										instancePath + "/front_matter/patient/age_years",
									schemaPath:
										"#/properties/front_matter/properties/patient/properties/age_years/type",
									keyword: "type",
									params: { type: "integer" },
									message: "must be integer",
								};
								if (vErrors === null) {
									vErrors = [err16];
								} else {
									vErrors.push(err16);
								}
								errors++;
							}
							if (typeof data5 == "number" && isFinite(data5)) {
								if (data5 > 130 || isNaN(data5)) {
									const err17 = {
										instancePath:
											instancePath + "/front_matter/patient/age_years",
										schemaPath:
											"#/properties/front_matter/properties/patient/properties/age_years/maximum",
										keyword: "maximum",
										params: { comparison: "<=", limit: 130 },
										message: "must be <= 130",
									};
									if (vErrors === null) {
										vErrors = [err17];
									} else {
										vErrors.push(err17);
									}
									errors++;
								}
								if (data5 < 0 || isNaN(data5)) {
									const err18 = {
										instancePath:
											instancePath + "/front_matter/patient/age_years",
										schemaPath:
											"#/properties/front_matter/properties/patient/properties/age_years/minimum",
										keyword: "minimum",
										params: { comparison: ">=", limit: 0 },
										message: "must be >= 0",
									};
									if (vErrors === null) {
										vErrors = [err18];
									} else {
										vErrors.push(err18);
									}
									errors++;
								}
							}
						}
						if (data1.sex !== undefined) {
							let data6 = data1.sex;
							if (typeof data6 !== "string") {
								const err19 = {
									instancePath: instancePath + "/front_matter/patient/sex",
									schemaPath:
										"#/properties/front_matter/properties/patient/properties/sex/type",
									keyword: "type",
									params: { type: "string" },
									message: "must be string",
								};
								if (vErrors === null) {
									vErrors = [err19];
								} else {
									vErrors.push(err19);
								}
								errors++;
							}
							if (
								!(
									data6 === "female" ||
									data6 === "male" ||
									data6 === "other" ||
									data6 === "unknown"
								)
							) {
								const err20 = {
									instancePath: instancePath + "/front_matter/patient/sex",
									schemaPath:
										"#/properties/front_matter/properties/patient/properties/sex/enum",
									keyword: "enum",
									params: {
										allowedValues:
											schema31.properties.front_matter.properties.patient
												.properties.sex.enum,
									},
									message: "must be equal to one of the allowed values",
								};
								if (vErrors === null) {
									vErrors = [err20];
								} else {
									vErrors.push(err20);
								}
								errors++;
							}
						}
						if (data1.pronoun !== undefined) {
							let data7 = data1.pronoun;
							if (data7 && typeof data7 == "object" && !Array.isArray(data7)) {
								if (data7.nom === undefined) {
									const err21 = {
										instancePath:
											instancePath + "/front_matter/patient/pronoun",
										schemaPath:
											"#/properties/front_matter/properties/patient/properties/pronoun/required",
										keyword: "required",
										params: { missingProperty: "nom" },
										message: "must have required property '" + "nom" + "'",
									};
									if (vErrors === null) {
										vErrors = [err21];
									} else {
										vErrors.push(err21);
									}
									errors++;
								}
								if (data7.gen === undefined) {
									const err22 = {
										instancePath:
											instancePath + "/front_matter/patient/pronoun",
										schemaPath:
											"#/properties/front_matter/properties/patient/properties/pronoun/required",
										keyword: "required",
										params: { missingProperty: "gen" },
										message: "must have required property '" + "gen" + "'",
									};
									if (vErrors === null) {
										vErrors = [err22];
									} else {
										vErrors.push(err22);
									}
									errors++;
								}
								if (data7.obj === undefined) {
									const err23 = {
										instancePath:
											instancePath + "/front_matter/patient/pronoun",
										schemaPath:
											"#/properties/front_matter/properties/patient/properties/pronoun/required",
										keyword: "required",
										params: { missingProperty: "obj" },
										message: "must have required property '" + "obj" + "'",
									};
									if (vErrors === null) {
										vErrors = [err23];
									} else {
										vErrors.push(err23);
									}
									errors++;
								}
								if (data7.refl === undefined) {
									const err24 = {
										instancePath:
											instancePath + "/front_matter/patient/pronoun",
										schemaPath:
											"#/properties/front_matter/properties/patient/properties/pronoun/required",
										keyword: "required",
										params: { missingProperty: "refl" },
										message: "must have required property '" + "refl" + "'",
									};
									if (vErrors === null) {
										vErrors = [err24];
									} else {
										vErrors.push(err24);
									}
									errors++;
								}
								for (const key3 in data7) {
									if (
										!(
											key3 === "nom" ||
											key3 === "gen" ||
											key3 === "obj" ||
											key3 === "refl"
										)
									) {
										const err25 = {
											instancePath:
												instancePath + "/front_matter/patient/pronoun",
											schemaPath:
												"#/properties/front_matter/properties/patient/properties/pronoun/additionalProperties",
											keyword: "additionalProperties",
											params: { additionalProperty: key3 },
											message: "must NOT have additional properties",
										};
										if (vErrors === null) {
											vErrors = [err25];
										} else {
											vErrors.push(err25);
										}
										errors++;
									}
								}
								if (data7.nom !== undefined) {
									if (typeof data7.nom !== "string") {
										const err26 = {
											instancePath:
												instancePath + "/front_matter/patient/pronoun/nom",
											schemaPath:
												"#/properties/front_matter/properties/patient/properties/pronoun/properties/nom/type",
											keyword: "type",
											params: { type: "string" },
											message: "must be string",
										};
										if (vErrors === null) {
											vErrors = [err26];
										} else {
											vErrors.push(err26);
										}
										errors++;
									}
								}
								if (data7.gen !== undefined) {
									if (typeof data7.gen !== "string") {
										const err27 = {
											instancePath:
												instancePath + "/front_matter/patient/pronoun/gen",
											schemaPath:
												"#/properties/front_matter/properties/patient/properties/pronoun/properties/gen/type",
											keyword: "type",
											params: { type: "string" },
											message: "must be string",
										};
										if (vErrors === null) {
											vErrors = [err27];
										} else {
											vErrors.push(err27);
										}
										errors++;
									}
								}
								if (data7.obj !== undefined) {
									if (typeof data7.obj !== "string") {
										const err28 = {
											instancePath:
												instancePath + "/front_matter/patient/pronoun/obj",
											schemaPath:
												"#/properties/front_matter/properties/patient/properties/pronoun/properties/obj/type",
											keyword: "type",
											params: { type: "string" },
											message: "must be string",
										};
										if (vErrors === null) {
											vErrors = [err28];
										} else {
											vErrors.push(err28);
										}
										errors++;
									}
								}
								if (data7.refl !== undefined) {
									if (typeof data7.refl !== "string") {
										const err29 = {
											instancePath:
												instancePath + "/front_matter/patient/pronoun/refl",
											schemaPath:
												"#/properties/front_matter/properties/patient/properties/pronoun/properties/refl/type",
											keyword: "type",
											params: { type: "string" },
											message: "must be string",
										};
										if (vErrors === null) {
											vErrors = [err29];
										} else {
											vErrors.push(err29);
										}
										errors++;
									}
								}
							} else {
								const err30 = {
									instancePath: instancePath + "/front_matter/patient/pronoun",
									schemaPath:
										"#/properties/front_matter/properties/patient/properties/pronoun/type",
									keyword: "type",
									params: { type: "object" },
									message: "must be object",
								};
								if (vErrors === null) {
									vErrors = [err30];
								} else {
									vErrors.push(err30);
								}
								errors++;
							}
						}
					} else {
						const err31 = {
							instancePath: instancePath + "/front_matter/patient",
							schemaPath: "#/properties/front_matter/properties/patient/type",
							keyword: "type",
							params: { type: "object" },
							message: "must be object",
						};
						if (vErrors === null) {
							vErrors = [err31];
						} else {
							vErrors.push(err31);
						}
						errors++;
					}
				}
				if (data0.encounter !== undefined) {
					let data12 = data0.encounter;
					if (data12 && typeof data12 == "object" && !Array.isArray(data12)) {
						for (const key4 in data12) {
							if (!(key4 === "datetime")) {
								const err32 = {
									instancePath: instancePath + "/front_matter/encounter",
									schemaPath:
										"#/properties/front_matter/properties/encounter/additionalProperties",
									keyword: "additionalProperties",
									params: { additionalProperty: key4 },
									message: "must NOT have additional properties",
								};
								if (vErrors === null) {
									vErrors = [err32];
								} else {
									vErrors.push(err32);
								}
								errors++;
							}
						}
						if (data12.datetime !== undefined) {
							if (typeof data12.datetime !== "string") {
								const err33 = {
									instancePath:
										instancePath + "/front_matter/encounter/datetime",
									schemaPath:
										"#/properties/front_matter/properties/encounter/properties/datetime/type",
									keyword: "type",
									params: { type: "string" },
									message: "must be string",
								};
								if (vErrors === null) {
									vErrors = [err33];
								} else {
									vErrors.push(err33);
								}
								errors++;
							}
						}
					} else {
						const err34 = {
							instancePath: instancePath + "/front_matter/encounter",
							schemaPath: "#/properties/front_matter/properties/encounter/type",
							keyword: "type",
							params: { type: "object" },
							message: "must be object",
						};
						if (vErrors === null) {
							vErrors = [err34];
						} else {
							vErrors.push(err34);
						}
						errors++;
					}
				}
				if (data0.diagnosis !== undefined) {
					let data14 = data0.diagnosis;
					if (Array.isArray(data14)) {
						const len0 = data14.length;
						for (let i0 = 0; i0 < len0; i0++) {
							if (typeof data14[i0] !== "string") {
								const err35 = {
									instancePath: instancePath + "/front_matter/diagnosis/" + i0,
									schemaPath:
										"#/properties/front_matter/properties/diagnosis/items/type",
									keyword: "type",
									params: { type: "string" },
									message: "must be string",
								};
								if (vErrors === null) {
									vErrors = [err35];
								} else {
									vErrors.push(err35);
								}
								errors++;
							}
						}
					} else {
						const err36 = {
							instancePath: instancePath + "/front_matter/diagnosis",
							schemaPath: "#/properties/front_matter/properties/diagnosis/type",
							keyword: "type",
							params: { type: "array" },
							message: "must be array",
						};
						if (vErrors === null) {
							vErrors = [err36];
						} else {
							vErrors.push(err36);
						}
						errors++;
					}
				}
				if (data0.staging !== undefined) {
					let data16 = data0.staging;
					if (data16 && typeof data16 == "object" && !Array.isArray(data16)) {
						if (data16.stage_group === undefined) {
							const err37 = {
								instancePath: instancePath + "/front_matter/staging",
								schemaPath:
									"#/properties/front_matter/properties/staging/required",
								keyword: "required",
								params: { missingProperty: "stage_group" },
								message: "must have required property '" + "stage_group" + "'",
							};
							if (vErrors === null) {
								vErrors = [err37];
							} else {
								vErrors.push(err37);
							}
							errors++;
						}
						if (data16.tnm === undefined) {
							const err38 = {
								instancePath: instancePath + "/front_matter/staging",
								schemaPath:
									"#/properties/front_matter/properties/staging/required",
								keyword: "required",
								params: { missingProperty: "tnm" },
								message: "must have required property '" + "tnm" + "'",
							};
							if (vErrors === null) {
								vErrors = [err38];
							} else {
								vErrors.push(err38);
							}
							errors++;
						}
						for (const key5 in data16) {
							if (!(key5 === "stage_group" || key5 === "tnm")) {
								const err39 = {
									instancePath: instancePath + "/front_matter/staging",
									schemaPath:
										"#/properties/front_matter/properties/staging/additionalProperties",
									keyword: "additionalProperties",
									params: { additionalProperty: key5 },
									message: "must NOT have additional properties",
								};
								if (vErrors === null) {
									vErrors = [err39];
								} else {
									vErrors.push(err39);
								}
								errors++;
							}
						}
						if (data16.stage_group !== undefined) {
							let data17 = data16.stage_group;
							if (typeof data17 === "string") {
								if (!pattern4.test(data17)) {
									const err40 = {
										instancePath:
											instancePath + "/front_matter/staging/stage_group",
										schemaPath:
											"#/properties/front_matter/properties/staging/properties/stage_group/pattern",
										keyword: "pattern",
										params: { pattern: "^Stage\\s*(0|I{1,3}(A|B|C)?|IV)$" },
										message:
											'must match pattern "' +
											"^Stage\\s*(0|I{1,3}(A|B|C)?|IV)$" +
											'"',
									};
									if (vErrors === null) {
										vErrors = [err40];
									} else {
										vErrors.push(err40);
									}
									errors++;
								}
							} else {
								const err41 = {
									instancePath:
										instancePath + "/front_matter/staging/stage_group",
									schemaPath:
										"#/properties/front_matter/properties/staging/properties/stage_group/type",
									keyword: "type",
									params: { type: "string" },
									message: "must be string",
								};
								if (vErrors === null) {
									vErrors = [err41];
								} else {
									vErrors.push(err41);
								}
								errors++;
							}
						}
						if (data16.tnm !== undefined) {
							let data18 = data16.tnm;
							if (
								data18 &&
								typeof data18 == "object" &&
								!Array.isArray(data18)
							) {
								if (data18.prefix === undefined) {
									const err42 = {
										instancePath: instancePath + "/front_matter/staging/tnm",
										schemaPath:
											"#/properties/front_matter/properties/staging/properties/tnm/required",
										keyword: "required",
										params: { missingProperty: "prefix" },
										message: "must have required property '" + "prefix" + "'",
									};
									if (vErrors === null) {
										vErrors = [err42];
									} else {
										vErrors.push(err42);
									}
									errors++;
								}
								if (data18.T === undefined) {
									const err43 = {
										instancePath: instancePath + "/front_matter/staging/tnm",
										schemaPath:
											"#/properties/front_matter/properties/staging/properties/tnm/required",
										keyword: "required",
										params: { missingProperty: "T" },
										message: "must have required property '" + "T" + "'",
									};
									if (vErrors === null) {
										vErrors = [err43];
									} else {
										vErrors.push(err43);
									}
									errors++;
								}
								if (data18.N === undefined) {
									const err44 = {
										instancePath: instancePath + "/front_matter/staging/tnm",
										schemaPath:
											"#/properties/front_matter/properties/staging/properties/tnm/required",
										keyword: "required",
										params: { missingProperty: "N" },
										message: "must have required property '" + "N" + "'",
									};
									if (vErrors === null) {
										vErrors = [err44];
									} else {
										vErrors.push(err44);
									}
									errors++;
								}
								if (data18.M === undefined) {
									const err45 = {
										instancePath: instancePath + "/front_matter/staging/tnm",
										schemaPath:
											"#/properties/front_matter/properties/staging/properties/tnm/required",
										keyword: "required",
										params: { missingProperty: "M" },
										message: "must have required property '" + "M" + "'",
									};
									if (vErrors === null) {
										vErrors = [err45];
									} else {
										vErrors.push(err45);
									}
									errors++;
								}
								for (const key6 in data18) {
									if (
										!(
											key6 === "prefix" ||
											key6 === "T" ||
											key6 === "N" ||
											key6 === "M"
										)
									) {
										const err46 = {
											instancePath: instancePath + "/front_matter/staging/tnm",
											schemaPath:
												"#/properties/front_matter/properties/staging/properties/tnm/additionalProperties",
											keyword: "additionalProperties",
											params: { additionalProperty: key6 },
											message: "must NOT have additional properties",
										};
										if (vErrors === null) {
											vErrors = [err46];
										} else {
											vErrors.push(err46);
										}
										errors++;
									}
								}
								if (data18.prefix !== undefined) {
									let data19 = data18.prefix;
									if (typeof data19 !== "string") {
										const err47 = {
											instancePath:
												instancePath + "/front_matter/staging/tnm/prefix",
											schemaPath:
												"#/properties/front_matter/properties/staging/properties/tnm/properties/prefix/type",
											keyword: "type",
											params: { type: "string" },
											message: "must be string",
										};
										if (vErrors === null) {
											vErrors = [err47];
										} else {
											vErrors.push(err47);
										}
										errors++;
									}
									if (
										!(
											data19 === "p" ||
											data19 === "c" ||
											data19 === "yp" ||
											data19 === "yc" ||
											data19 === "x"
										)
									) {
										const err48 = {
											instancePath:
												instancePath + "/front_matter/staging/tnm/prefix",
											schemaPath:
												"#/properties/front_matter/properties/staging/properties/tnm/properties/prefix/enum",
											keyword: "enum",
											params: {
												allowedValues:
													schema31.properties.front_matter.properties.staging
														.properties.tnm.properties.prefix.enum,
											},
											message: "must be equal to one of the allowed values",
										};
										if (vErrors === null) {
											vErrors = [err48];
										} else {
											vErrors.push(err48);
										}
										errors++;
									}
								}
								if (data18.T !== undefined) {
									let data20 = data18.T;
									if (typeof data20 === "string") {
										if (!pattern5.test(data20)) {
											const err49 = {
												instancePath:
													instancePath + "/front_matter/staging/tnm/T",
												schemaPath:
													"#/properties/front_matter/properties/staging/properties/tnm/properties/T/pattern",
												keyword: "pattern",
												params: { pattern: "^T(is|1a|1b|1c|1|2|3|4[abcd]?)$" },
												message:
													'must match pattern "' +
													"^T(is|1a|1b|1c|1|2|3|4[abcd]?)$" +
													'"',
											};
											if (vErrors === null) {
												vErrors = [err49];
											} else {
												vErrors.push(err49);
											}
											errors++;
										}
									} else {
										const err50 = {
											instancePath:
												instancePath + "/front_matter/staging/tnm/T",
											schemaPath:
												"#/properties/front_matter/properties/staging/properties/tnm/properties/T/type",
											keyword: "type",
											params: { type: "string" },
											message: "must be string",
										};
										if (vErrors === null) {
											vErrors = [err50];
										} else {
											vErrors.push(err50);
										}
										errors++;
									}
								}
								if (data18.N !== undefined) {
									let data21 = data18.N;
									if (typeof data21 === "string") {
										if (!pattern6.test(data21)) {
											const err51 = {
												instancePath:
													instancePath + "/front_matter/staging/tnm/N",
												schemaPath:
													"#/properties/front_matter/properties/staging/properties/tnm/properties/N/pattern",
												keyword: "pattern",
												params: { pattern: "^N(0|1[abc]?|2[ab]?|3[abc]?)$" },
												message:
													'must match pattern "' +
													"^N(0|1[abc]?|2[ab]?|3[abc]?)$" +
													'"',
											};
											if (vErrors === null) {
												vErrors = [err51];
											} else {
												vErrors.push(err51);
											}
											errors++;
										}
									} else {
										const err52 = {
											instancePath:
												instancePath + "/front_matter/staging/tnm/N",
											schemaPath:
												"#/properties/front_matter/properties/staging/properties/tnm/properties/N/type",
											keyword: "type",
											params: { type: "string" },
											message: "must be string",
										};
										if (vErrors === null) {
											vErrors = [err52];
										} else {
											vErrors.push(err52);
										}
										errors++;
									}
								}
								if (data18.M !== undefined) {
									let data22 = data18.M;
									if (typeof data22 === "string") {
										if (!pattern7.test(data22)) {
											const err53 = {
												instancePath:
													instancePath + "/front_matter/staging/tnm/M",
												schemaPath:
													"#/properties/front_matter/properties/staging/properties/tnm/properties/M/pattern",
												keyword: "pattern",
												params: { pattern: "^M(0|1|X)$" },
												message: 'must match pattern "' + "^M(0|1|X)$" + '"',
											};
											if (vErrors === null) {
												vErrors = [err53];
											} else {
												vErrors.push(err53);
											}
											errors++;
										}
									} else {
										const err54 = {
											instancePath:
												instancePath + "/front_matter/staging/tnm/M",
											schemaPath:
												"#/properties/front_matter/properties/staging/properties/tnm/properties/M/type",
											keyword: "type",
											params: { type: "string" },
											message: "must be string",
										};
										if (vErrors === null) {
											vErrors = [err54];
										} else {
											vErrors.push(err54);
										}
										errors++;
									}
								}
							} else {
								const err55 = {
									instancePath: instancePath + "/front_matter/staging/tnm",
									schemaPath:
										"#/properties/front_matter/properties/staging/properties/tnm/type",
									keyword: "type",
									params: { type: "object" },
									message: "must be object",
								};
								if (vErrors === null) {
									vErrors = [err55];
								} else {
									vErrors.push(err55);
								}
								errors++;
							}
						}
					} else {
						const err56 = {
							instancePath: instancePath + "/front_matter/staging",
							schemaPath: "#/properties/front_matter/properties/staging/type",
							keyword: "type",
							params: { type: "object" },
							message: "must be object",
						};
						if (vErrors === null) {
							vErrors = [err56];
						} else {
							vErrors.push(err56);
						}
						errors++;
					}
				}
				if (data0.pathology !== undefined) {
					let data23 = data0.pathology;
					if (data23 && typeof data23 == "object" && !Array.isArray(data23)) {
						if (data23.histology === undefined) {
							const err57 = {
								instancePath: instancePath + "/front_matter/pathology",
								schemaPath:
									"#/properties/front_matter/properties/pathology/required",
								keyword: "required",
								params: { missingProperty: "histology" },
								message: "must have required property '" + "histology" + "'",
							};
							if (vErrors === null) {
								vErrors = [err57];
							} else {
								vErrors.push(err57);
							}
							errors++;
						}
						if (data23.grade === undefined) {
							const err58 = {
								instancePath: instancePath + "/front_matter/pathology",
								schemaPath:
									"#/properties/front_matter/properties/pathology/required",
								keyword: "required",
								params: { missingProperty: "grade" },
								message: "must have required property '" + "grade" + "'",
							};
							if (vErrors === null) {
								vErrors = [err58];
							} else {
								vErrors.push(err58);
							}
							errors++;
						}
						if (data23.tumor_size_cm === undefined) {
							const err59 = {
								instancePath: instancePath + "/front_matter/pathology",
								schemaPath:
									"#/properties/front_matter/properties/pathology/required",
								keyword: "required",
								params: { missingProperty: "tumor_size_cm" },
								message:
									"must have required property '" + "tumor_size_cm" + "'",
							};
							if (vErrors === null) {
								vErrors = [err59];
							} else {
								vErrors.push(err59);
							}
							errors++;
						}
						if (data23.nodes_examined === undefined) {
							const err60 = {
								instancePath: instancePath + "/front_matter/pathology",
								schemaPath:
									"#/properties/front_matter/properties/pathology/required",
								keyword: "required",
								params: { missingProperty: "nodes_examined" },
								message:
									"must have required property '" + "nodes_examined" + "'",
							};
							if (vErrors === null) {
								vErrors = [err60];
							} else {
								vErrors.push(err60);
							}
							errors++;
						}
						if (data23.nodes_positive === undefined) {
							const err61 = {
								instancePath: instancePath + "/front_matter/pathology",
								schemaPath:
									"#/properties/front_matter/properties/pathology/required",
								keyword: "required",
								params: { missingProperty: "nodes_positive" },
								message:
									"must have required property '" + "nodes_positive" + "'",
							};
							if (vErrors === null) {
								vErrors = [err61];
							} else {
								vErrors.push(err61);
							}
							errors++;
						}
						for (const key7 in data23) {
							if (
								!(
									key7 === "histology" ||
									key7 === "grade" ||
									key7 === "tumor_size_cm" ||
									key7 === "dcis_present" ||
									key7 === "margins" ||
									key7 === "nodes_examined" ||
									key7 === "nodes_positive"
								)
							) {
								const err62 = {
									instancePath: instancePath + "/front_matter/pathology",
									schemaPath:
										"#/properties/front_matter/properties/pathology/additionalProperties",
									keyword: "additionalProperties",
									params: { additionalProperty: key7 },
									message: "must NOT have additional properties",
								};
								if (vErrors === null) {
									vErrors = [err62];
								} else {
									vErrors.push(err62);
								}
								errors++;
							}
						}
						if (data23.histology !== undefined) {
							if (typeof data23.histology !== "string") {
								const err63 = {
									instancePath:
										instancePath + "/front_matter/pathology/histology",
									schemaPath:
										"#/properties/front_matter/properties/pathology/properties/histology/type",
									keyword: "type",
									params: { type: "string" },
									message: "must be string",
								};
								if (vErrors === null) {
									vErrors = [err63];
								} else {
									vErrors.push(err63);
								}
								errors++;
							}
						}
						if (data23.grade !== undefined) {
							let data25 = data23.grade;
							if (typeof data25 === "string") {
								if (!pattern8.test(data25)) {
									const err64 = {
										instancePath:
											instancePath + "/front_matter/pathology/grade",
										schemaPath:
											"#/properties/front_matter/properties/pathology/properties/grade/pattern",
										keyword: "pattern",
										params: { pattern: "^[123]$" },
										message: 'must match pattern "' + "^[123]$" + '"',
									};
									if (vErrors === null) {
										vErrors = [err64];
									} else {
										vErrors.push(err64);
									}
									errors++;
								}
							} else {
								const err65 = {
									instancePath: instancePath + "/front_matter/pathology/grade",
									schemaPath:
										"#/properties/front_matter/properties/pathology/properties/grade/type",
									keyword: "type",
									params: { type: "string" },
									message: "must be string",
								};
								if (vErrors === null) {
									vErrors = [err65];
								} else {
									vErrors.push(err65);
								}
								errors++;
							}
						}
						if (data23.tumor_size_cm !== undefined) {
							let data26 = data23.tumor_size_cm;
							if (typeof data26 == "number" && isFinite(data26)) {
								if (data26 > 20 || isNaN(data26)) {
									const err66 = {
										instancePath:
											instancePath + "/front_matter/pathology/tumor_size_cm",
										schemaPath:
											"#/properties/front_matter/properties/pathology/properties/tumor_size_cm/maximum",
										keyword: "maximum",
										params: { comparison: "<=", limit: 20 },
										message: "must be <= 20",
									};
									if (vErrors === null) {
										vErrors = [err66];
									} else {
										vErrors.push(err66);
									}
									errors++;
								}
								if (data26 < 0 || isNaN(data26)) {
									const err67 = {
										instancePath:
											instancePath + "/front_matter/pathology/tumor_size_cm",
										schemaPath:
											"#/properties/front_matter/properties/pathology/properties/tumor_size_cm/minimum",
										keyword: "minimum",
										params: { comparison: ">=", limit: 0 },
										message: "must be >= 0",
									};
									if (vErrors === null) {
										vErrors = [err67];
									} else {
										vErrors.push(err67);
									}
									errors++;
								}
							} else {
								const err68 = {
									instancePath:
										instancePath + "/front_matter/pathology/tumor_size_cm",
									schemaPath:
										"#/properties/front_matter/properties/pathology/properties/tumor_size_cm/type",
									keyword: "type",
									params: { type: "number" },
									message: "must be number",
								};
								if (vErrors === null) {
									vErrors = [err68];
								} else {
									vErrors.push(err68);
								}
								errors++;
							}
						}
						if (data23.dcis_present !== undefined) {
							if (typeof data23.dcis_present !== "boolean") {
								const err69 = {
									instancePath:
										instancePath + "/front_matter/pathology/dcis_present",
									schemaPath:
										"#/properties/front_matter/properties/pathology/properties/dcis_present/type",
									keyword: "type",
									params: { type: "boolean" },
									message: "must be boolean",
								};
								if (vErrors === null) {
									vErrors = [err69];
								} else {
									vErrors.push(err69);
								}
								errors++;
							}
						}
						if (data23.margins !== undefined) {
							if (typeof data23.margins !== "string") {
								const err70 = {
									instancePath:
										instancePath + "/front_matter/pathology/margins",
									schemaPath:
										"#/properties/front_matter/properties/pathology/properties/margins/type",
									keyword: "type",
									params: { type: "string" },
									message: "must be string",
								};
								if (vErrors === null) {
									vErrors = [err70];
								} else {
									vErrors.push(err70);
								}
								errors++;
							}
						}
						if (data23.nodes_examined !== undefined) {
							let data29 = data23.nodes_examined;
							if (
								!(
									typeof data29 == "number" &&
									!(data29 % 1) &&
									!isNaN(data29) &&
									isFinite(data29)
								)
							) {
								const err71 = {
									instancePath:
										instancePath + "/front_matter/pathology/nodes_examined",
									schemaPath:
										"#/properties/front_matter/properties/pathology/properties/nodes_examined/type",
									keyword: "type",
									params: { type: "integer" },
									message: "must be integer",
								};
								if (vErrors === null) {
									vErrors = [err71];
								} else {
									vErrors.push(err71);
								}
								errors++;
							}
							if (typeof data29 == "number" && isFinite(data29)) {
								if (data29 < 0 || isNaN(data29)) {
									const err72 = {
										instancePath:
											instancePath + "/front_matter/pathology/nodes_examined",
										schemaPath:
											"#/properties/front_matter/properties/pathology/properties/nodes_examined/minimum",
										keyword: "minimum",
										params: { comparison: ">=", limit: 0 },
										message: "must be >= 0",
									};
									if (vErrors === null) {
										vErrors = [err72];
									} else {
										vErrors.push(err72);
									}
									errors++;
								}
							}
						}
						if (data23.nodes_positive !== undefined) {
							let data30 = data23.nodes_positive;
							if (
								!(
									typeof data30 == "number" &&
									!(data30 % 1) &&
									!isNaN(data30) &&
									isFinite(data30)
								)
							) {
								const err73 = {
									instancePath:
										instancePath + "/front_matter/pathology/nodes_positive",
									schemaPath:
										"#/properties/front_matter/properties/pathology/properties/nodes_positive/type",
									keyword: "type",
									params: { type: "integer" },
									message: "must be integer",
								};
								if (vErrors === null) {
									vErrors = [err73];
								} else {
									vErrors.push(err73);
								}
								errors++;
							}
							if (typeof data30 == "number" && isFinite(data30)) {
								if (data30 < 0 || isNaN(data30)) {
									const err74 = {
										instancePath:
											instancePath + "/front_matter/pathology/nodes_positive",
										schemaPath:
											"#/properties/front_matter/properties/pathology/properties/nodes_positive/minimum",
										keyword: "minimum",
										params: { comparison: ">=", limit: 0 },
										message: "must be >= 0",
									};
									if (vErrors === null) {
										vErrors = [err74];
									} else {
										vErrors.push(err74);
									}
									errors++;
								}
							}
						}
					} else {
						const err75 = {
							instancePath: instancePath + "/front_matter/pathology",
							schemaPath: "#/properties/front_matter/properties/pathology/type",
							keyword: "type",
							params: { type: "object" },
							message: "must be object",
						};
						if (vErrors === null) {
							vErrors = [err75];
						} else {
							vErrors.push(err75);
						}
						errors++;
					}
				}
				if (data0.receptors !== undefined) {
					let data31 = data0.receptors;
					if (data31 && typeof data31 == "object" && !Array.isArray(data31)) {
						if (data31.ER === undefined) {
							const err76 = {
								instancePath: instancePath + "/front_matter/receptors",
								schemaPath:
									"#/properties/front_matter/properties/receptors/required",
								keyword: "required",
								params: { missingProperty: "ER" },
								message: "must have required property '" + "ER" + "'",
							};
							if (vErrors === null) {
								vErrors = [err76];
							} else {
								vErrors.push(err76);
							}
							errors++;
						}
						if (data31.PR === undefined) {
							const err77 = {
								instancePath: instancePath + "/front_matter/receptors",
								schemaPath:
									"#/properties/front_matter/properties/receptors/required",
								keyword: "required",
								params: { missingProperty: "PR" },
								message: "must have required property '" + "PR" + "'",
							};
							if (vErrors === null) {
								vErrors = [err77];
							} else {
								vErrors.push(err77);
							}
							errors++;
						}
						if (data31.her2 === undefined) {
							const err78 = {
								instancePath: instancePath + "/front_matter/receptors",
								schemaPath:
									"#/properties/front_matter/properties/receptors/required",
								keyword: "required",
								params: { missingProperty: "her2" },
								message: "must have required property '" + "her2" + "'",
							};
							if (vErrors === null) {
								vErrors = [err78];
							} else {
								vErrors.push(err78);
							}
							errors++;
						}
						for (const key8 in data31) {
							if (!(key8 === "ER" || key8 === "PR" || key8 === "her2")) {
								const err79 = {
									instancePath: instancePath + "/front_matter/receptors",
									schemaPath:
										"#/properties/front_matter/properties/receptors/additionalProperties",
									keyword: "additionalProperties",
									params: { additionalProperty: key8 },
									message: "must NOT have additional properties",
								};
								if (vErrors === null) {
									vErrors = [err79];
								} else {
									vErrors.push(err79);
								}
								errors++;
							}
						}
						if (data31.ER !== undefined) {
							let data32 = data31.ER;
							if (typeof data32 === "string") {
								if (!pattern9.test(data32)) {
									const err80 = {
										instancePath: instancePath + "/front_matter/receptors/ER",
										schemaPath:
											"#/properties/front_matter/properties/receptors/properties/ER/pattern",
										keyword: "pattern",
										params: { pattern: "^[0-8]/8$" },
										message: 'must match pattern "' + "^[0-8]/8$" + '"',
									};
									if (vErrors === null) {
										vErrors = [err80];
									} else {
										vErrors.push(err80);
									}
									errors++;
								}
							} else {
								const err81 = {
									instancePath: instancePath + "/front_matter/receptors/ER",
									schemaPath:
										"#/properties/front_matter/properties/receptors/properties/ER/type",
									keyword: "type",
									params: { type: "string" },
									message: "must be string",
								};
								if (vErrors === null) {
									vErrors = [err81];
								} else {
									vErrors.push(err81);
								}
								errors++;
							}
						}
						if (data31.PR !== undefined) {
							let data33 = data31.PR;
							if (typeof data33 === "string") {
								if (!pattern9.test(data33)) {
									const err82 = {
										instancePath: instancePath + "/front_matter/receptors/PR",
										schemaPath:
											"#/properties/front_matter/properties/receptors/properties/PR/pattern",
										keyword: "pattern",
										params: { pattern: "^[0-8]/8$" },
										message: 'must match pattern "' + "^[0-8]/8$" + '"',
									};
									if (vErrors === null) {
										vErrors = [err82];
									} else {
										vErrors.push(err82);
									}
									errors++;
								}
							} else {
								const err83 = {
									instancePath: instancePath + "/front_matter/receptors/PR",
									schemaPath:
										"#/properties/front_matter/properties/receptors/properties/PR/type",
									keyword: "type",
									params: { type: "string" },
									message: "must be string",
								};
								if (vErrors === null) {
									vErrors = [err83];
								} else {
									vErrors.push(err83);
								}
								errors++;
							}
						}
						if (data31.her2 !== undefined) {
							let data34 = data31.her2;
							const _errs81 = errors;
							let valid12 = true;
							const _errs82 = errors;
							if (
								data34 &&
								typeof data34 == "object" &&
								!Array.isArray(data34)
							) {
								let missing0;
								if (data34.label === undefined && (missing0 = "label")) {
									const err84 = {};
									if (vErrors === null) {
										vErrors = [err84];
									} else {
										vErrors.push(err84);
									}
									errors++;
								}
							}
							var _valid0 = _errs82 === errors;
							errors = _errs81;
							if (vErrors !== null) {
								if (_errs81) {
									vErrors.length = _errs81;
								} else {
									vErrors = null;
								}
							}
							if (_valid0) {
								const _errs83 = errors;
								if (
									data34 &&
									typeof data34 == "object" &&
									!Array.isArray(data34)
								) {
									if (data34.raw_text === undefined) {
										const err85 = {
											instancePath:
												instancePath + "/front_matter/receptors/her2",
											schemaPath:
												"#/properties/front_matter/properties/receptors/properties/her2/allOf/0/then/required",
											keyword: "required",
											params: { missingProperty: "raw_text" },
											message:
												"must have required property '" + "raw_text" + "'",
										};
										if (vErrors === null) {
											vErrors = [err85];
										} else {
											vErrors.push(err85);
										}
										errors++;
									}
								}
								var _valid0 = _errs83 === errors;
								valid12 = _valid0;
							}
							if (!valid12) {
								const err86 = {
									instancePath: instancePath + "/front_matter/receptors/her2",
									schemaPath:
										"#/properties/front_matter/properties/receptors/properties/her2/allOf/0/if",
									keyword: "if",
									params: { failingKeyword: "then" },
									message: 'must match "then" schema',
								};
								if (vErrors === null) {
									vErrors = [err86];
								} else {
									vErrors.push(err86);
								}
								errors++;
							}
							if (
								data34 &&
								typeof data34 == "object" &&
								!Array.isArray(data34)
							) {
								for (const key9 in data34) {
									if (
										!(
											key9 === "raw_text" ||
											key9 === "label" ||
											key9 === "detail" ||
											key9 === "ihc_score" ||
											key9 === "fish_result"
										)
									) {
										const err87 = {
											instancePath:
												instancePath + "/front_matter/receptors/her2",
											schemaPath:
												"#/properties/front_matter/properties/receptors/properties/her2/additionalProperties",
											keyword: "additionalProperties",
											params: { additionalProperty: key9 },
											message: "must NOT have additional properties",
										};
										if (vErrors === null) {
											vErrors = [err87];
										} else {
											vErrors.push(err87);
										}
										errors++;
									}
								}
								if (data34.raw_text !== undefined) {
									if (typeof data34.raw_text !== "string") {
										const err88 = {
											instancePath:
												instancePath + "/front_matter/receptors/her2/raw_text",
											schemaPath:
												"#/properties/front_matter/properties/receptors/properties/her2/properties/raw_text/type",
											keyword: "type",
											params: { type: "string" },
											message: "must be string",
										};
										if (vErrors === null) {
											vErrors = [err88];
										} else {
											vErrors.push(err88);
										}
										errors++;
									}
								}
								if (data34.label !== undefined) {
									let data36 = data34.label;
									if (typeof data36 !== "string") {
										const err89 = {
											instancePath:
												instancePath + "/front_matter/receptors/her2/label",
											schemaPath:
												"#/properties/front_matter/properties/receptors/properties/her2/properties/label/type",
											keyword: "type",
											params: { type: "string" },
											message: "must be string",
										};
										if (vErrors === null) {
											vErrors = [err89];
										} else {
											vErrors.push(err89);
										}
										errors++;
									}
									if (
										!(
											data36 === "HER2-positive" ||
											data36 === "HER2-negative" ||
											data36 === "HER2 status equivocal/unknown"
										)
									) {
										const err90 = {
											instancePath:
												instancePath + "/front_matter/receptors/her2/label",
											schemaPath:
												"#/properties/front_matter/properties/receptors/properties/her2/properties/label/enum",
											keyword: "enum",
											params: {
												allowedValues:
													schema31.properties.front_matter.properties.receptors
														.properties.her2.properties.label.enum,
											},
											message: "must be equal to one of the allowed values",
										};
										if (vErrors === null) {
											vErrors = [err90];
										} else {
											vErrors.push(err90);
										}
										errors++;
									}
								}
								if (data34.detail !== undefined) {
									if (typeof data34.detail !== "string") {
										const err91 = {
											instancePath:
												instancePath + "/front_matter/receptors/her2/detail",
											schemaPath:
												"#/properties/front_matter/properties/receptors/properties/her2/properties/detail/type",
											keyword: "type",
											params: { type: "string" },
											message: "must be string",
										};
										if (vErrors === null) {
											vErrors = [err91];
										} else {
											vErrors.push(err91);
										}
										errors++;
									}
								}
								if (data34.ihc_score !== undefined) {
									let data38 = data34.ihc_score;
									if (typeof data38 !== "string" && data38 !== null) {
										const err92 = {
											instancePath:
												instancePath + "/front_matter/receptors/her2/ihc_score",
											schemaPath:
												"#/properties/front_matter/properties/receptors/properties/her2/properties/ihc_score/type",
											keyword: "type",
											params: {
												type: schema31.properties.front_matter.properties
													.receptors.properties.her2.properties.ihc_score.type,
											},
											message: "must be string,null",
										};
										if (vErrors === null) {
											vErrors = [err92];
										} else {
											vErrors.push(err92);
										}
										errors++;
									}
									if (
										!(
											data38 === "0" ||
											data38 === "1+" ||
											data38 === "2+" ||
											data38 === "3+" ||
											data38 === null
										)
									) {
										const err93 = {
											instancePath:
												instancePath + "/front_matter/receptors/her2/ihc_score",
											schemaPath:
												"#/properties/front_matter/properties/receptors/properties/her2/properties/ihc_score/enum",
											keyword: "enum",
											params: {
												allowedValues:
													schema31.properties.front_matter.properties.receptors
														.properties.her2.properties.ihc_score.enum,
											},
											message: "must be equal to one of the allowed values",
										};
										if (vErrors === null) {
											vErrors = [err93];
										} else {
											vErrors.push(err93);
										}
										errors++;
									}
								}
								if (data34.fish_result !== undefined) {
									let data39 = data34.fish_result;
									if (typeof data39 !== "string" && data39 !== null) {
										const err94 = {
											instancePath:
												instancePath +
												"/front_matter/receptors/her2/fish_result",
											schemaPath:
												"#/properties/front_matter/properties/receptors/properties/her2/properties/fish_result/type",
											keyword: "type",
											params: {
												type: schema31.properties.front_matter.properties
													.receptors.properties.her2.properties.fish_result
													.type,
											},
											message: "must be string,null",
										};
										if (vErrors === null) {
											vErrors = [err94];
										} else {
											vErrors.push(err94);
										}
										errors++;
									}
									if (
										!(
											data39 === "positive" ||
											data39 === "negative" ||
											data39 === "equivocal" ||
											data39 === "not_done" ||
											data39 === null
										)
									) {
										const err95 = {
											instancePath:
												instancePath +
												"/front_matter/receptors/her2/fish_result",
											schemaPath:
												"#/properties/front_matter/properties/receptors/properties/her2/properties/fish_result/enum",
											keyword: "enum",
											params: {
												allowedValues:
													schema31.properties.front_matter.properties.receptors
														.properties.her2.properties.fish_result.enum,
											},
											message: "must be equal to one of the allowed values",
										};
										if (vErrors === null) {
											vErrors = [err95];
										} else {
											vErrors.push(err95);
										}
										errors++;
									}
								}
							} else {
								const err96 = {
									instancePath: instancePath + "/front_matter/receptors/her2",
									schemaPath:
										"#/properties/front_matter/properties/receptors/properties/her2/type",
									keyword: "type",
									params: { type: "object" },
									message: "must be object",
								};
								if (vErrors === null) {
									vErrors = [err96];
								} else {
									vErrors.push(err96);
								}
								errors++;
							}
						}
					} else {
						const err97 = {
							instancePath: instancePath + "/front_matter/receptors",
							schemaPath: "#/properties/front_matter/properties/receptors/type",
							keyword: "type",
							params: { type: "object" },
							message: "must be object",
						};
						if (vErrors === null) {
							vErrors = [err97];
						} else {
							vErrors.push(err97);
						}
						errors++;
					}
				}
				if (data0.oncotype !== undefined) {
					let data40 = data0.oncotype;
					const _errs98 = errors;
					let valid15 = true;
					const _errs99 = errors;
					if (data40 && typeof data40 == "object" && !Array.isArray(data40)) {
						let missing1;
						if (data40.score === undefined && (missing1 = "score")) {
							const err98 = {};
							if (vErrors === null) {
								vErrors = [err98];
							} else {
								vErrors.push(err98);
							}
							errors++;
						}
					}
					var _valid1 = _errs99 === errors;
					errors = _errs98;
					if (vErrors !== null) {
						if (_errs98) {
							vErrors.length = _errs98;
						} else {
							vErrors = null;
						}
					}
					if (_valid1) {
						const _errs100 = errors;
						if (data40 && typeof data40 == "object" && !Array.isArray(data40)) {
							if (data40.risk_yr === undefined) {
								const err99 = {
									instancePath: instancePath + "/front_matter/oncotype",
									schemaPath:
										"#/properties/front_matter/properties/oncotype/allOf/0/then/required",
									keyword: "required",
									params: { missingProperty: "risk_yr" },
									message: "must have required property '" + "risk_yr" + "'",
								};
								if (vErrors === null) {
									vErrors = [err99];
								} else {
									vErrors.push(err99);
								}
								errors++;
							}
							if (data40.risk_percent === undefined) {
								const err100 = {
									instancePath: instancePath + "/front_matter/oncotype",
									schemaPath:
										"#/properties/front_matter/properties/oncotype/allOf/0/then/required",
									keyword: "required",
									params: { missingProperty: "risk_percent" },
									message:
										"must have required property '" + "risk_percent" + "'",
								};
								if (vErrors === null) {
									vErrors = [err100];
								} else {
									vErrors.push(err100);
								}
								errors++;
							}
						}
						var _valid1 = _errs100 === errors;
						valid15 = _valid1;
					}
					if (!valid15) {
						const err101 = {
							instancePath: instancePath + "/front_matter/oncotype",
							schemaPath:
								"#/properties/front_matter/properties/oncotype/allOf/0/if",
							keyword: "if",
							params: { failingKeyword: "then" },
							message: 'must match "then" schema',
						};
						if (vErrors === null) {
							vErrors = [err101];
						} else {
							vErrors.push(err101);
						}
						errors++;
					}
					if (data40 && typeof data40 == "object" && !Array.isArray(data40)) {
						for (const key10 in data40) {
							if (
								!(
									key10 === "score" ||
									key10 === "risk_yr" ||
									key10 === "risk_percent" ||
									key10 === "absolute_benefit_percent"
								)
							) {
								const err102 = {
									instancePath: instancePath + "/front_matter/oncotype",
									schemaPath:
										"#/properties/front_matter/properties/oncotype/additionalProperties",
									keyword: "additionalProperties",
									params: { additionalProperty: key10 },
									message: "must NOT have additional properties",
								};
								if (vErrors === null) {
									vErrors = [err102];
								} else {
									vErrors.push(err102);
								}
								errors++;
							}
						}
						if (data40.score !== undefined) {
							let data41 = data40.score;
							if (typeof data41 == "number" && isFinite(data41)) {
								if (data41 > 100 || isNaN(data41)) {
									const err103 = {
										instancePath: instancePath + "/front_matter/oncotype/score",
										schemaPath:
											"#/properties/front_matter/properties/oncotype/properties/score/maximum",
										keyword: "maximum",
										params: { comparison: "<=", limit: 100 },
										message: "must be <= 100",
									};
									if (vErrors === null) {
										vErrors = [err103];
									} else {
										vErrors.push(err103);
									}
									errors++;
								}
								if (data41 < 0 || isNaN(data41)) {
									const err104 = {
										instancePath: instancePath + "/front_matter/oncotype/score",
										schemaPath:
											"#/properties/front_matter/properties/oncotype/properties/score/minimum",
										keyword: "minimum",
										params: { comparison: ">=", limit: 0 },
										message: "must be >= 0",
									};
									if (vErrors === null) {
										vErrors = [err104];
									} else {
										vErrors.push(err104);
									}
									errors++;
								}
							} else {
								const err105 = {
									instancePath: instancePath + "/front_matter/oncotype/score",
									schemaPath:
										"#/properties/front_matter/properties/oncotype/properties/score/type",
									keyword: "type",
									params: { type: "number" },
									message: "must be number",
								};
								if (vErrors === null) {
									vErrors = [err105];
								} else {
									vErrors.push(err105);
								}
								errors++;
							}
						}
						if (data40.risk_yr !== undefined) {
							let data42 = data40.risk_yr;
							if (
								!(
									typeof data42 == "number" &&
									!(data42 % 1) &&
									!isNaN(data42) &&
									isFinite(data42)
								)
							) {
								const err106 = {
									instancePath: instancePath + "/front_matter/oncotype/risk_yr",
									schemaPath:
										"#/properties/front_matter/properties/oncotype/properties/risk_yr/type",
									keyword: "type",
									params: { type: "integer" },
									message: "must be integer",
								};
								if (vErrors === null) {
									vErrors = [err106];
								} else {
									vErrors.push(err106);
								}
								errors++;
							}
							if (typeof data42 == "number" && isFinite(data42)) {
								if (data42 > 100 || isNaN(data42)) {
									const err107 = {
										instancePath:
											instancePath + "/front_matter/oncotype/risk_yr",
										schemaPath:
											"#/properties/front_matter/properties/oncotype/properties/risk_yr/maximum",
										keyword: "maximum",
										params: { comparison: "<=", limit: 100 },
										message: "must be <= 100",
									};
									if (vErrors === null) {
										vErrors = [err107];
									} else {
										vErrors.push(err107);
									}
									errors++;
								}
								if (data42 < 1 || isNaN(data42)) {
									const err108 = {
										instancePath:
											instancePath + "/front_matter/oncotype/risk_yr",
										schemaPath:
											"#/properties/front_matter/properties/oncotype/properties/risk_yr/minimum",
										keyword: "minimum",
										params: { comparison: ">=", limit: 1 },
										message: "must be >= 1",
									};
									if (vErrors === null) {
										vErrors = [err108];
									} else {
										vErrors.push(err108);
									}
									errors++;
								}
							}
						}
						if (data40.risk_percent !== undefined) {
							let data43 = data40.risk_percent;
							if (typeof data43 == "number" && isFinite(data43)) {
								if (data43 > 100 || isNaN(data43)) {
									const err109 = {
										instancePath:
											instancePath + "/front_matter/oncotype/risk_percent",
										schemaPath:
											"#/properties/front_matter/properties/oncotype/properties/risk_percent/maximum",
										keyword: "maximum",
										params: { comparison: "<=", limit: 100 },
										message: "must be <= 100",
									};
									if (vErrors === null) {
										vErrors = [err109];
									} else {
										vErrors.push(err109);
									}
									errors++;
								}
								if (data43 < 0 || isNaN(data43)) {
									const err110 = {
										instancePath:
											instancePath + "/front_matter/oncotype/risk_percent",
										schemaPath:
											"#/properties/front_matter/properties/oncotype/properties/risk_percent/minimum",
										keyword: "minimum",
										params: { comparison: ">=", limit: 0 },
										message: "must be >= 0",
									};
									if (vErrors === null) {
										vErrors = [err110];
									} else {
										vErrors.push(err110);
									}
									errors++;
								}
							} else {
								const err111 = {
									instancePath:
										instancePath + "/front_matter/oncotype/risk_percent",
									schemaPath:
										"#/properties/front_matter/properties/oncotype/properties/risk_percent/type",
									keyword: "type",
									params: { type: "number" },
									message: "must be number",
								};
								if (vErrors === null) {
									vErrors = [err111];
								} else {
									vErrors.push(err111);
								}
								errors++;
							}
						}
						if (data40.absolute_benefit_percent !== undefined) {
							let data44 = data40.absolute_benefit_percent;
							if (typeof data44 == "number" && isFinite(data44)) {
								if (data44 > 100 || isNaN(data44)) {
									const err112 = {
										instancePath:
											instancePath +
											"/front_matter/oncotype/absolute_benefit_percent",
										schemaPath:
											"#/properties/front_matter/properties/oncotype/properties/absolute_benefit_percent/maximum",
										keyword: "maximum",
										params: { comparison: "<=", limit: 100 },
										message: "must be <= 100",
									};
									if (vErrors === null) {
										vErrors = [err112];
									} else {
										vErrors.push(err112);
									}
									errors++;
								}
								if (data44 < 0 || isNaN(data44)) {
									const err113 = {
										instancePath:
											instancePath +
											"/front_matter/oncotype/absolute_benefit_percent",
										schemaPath:
											"#/properties/front_matter/properties/oncotype/properties/absolute_benefit_percent/minimum",
										keyword: "minimum",
										params: { comparison: ">=", limit: 0 },
										message: "must be >= 0",
									};
									if (vErrors === null) {
										vErrors = [err113];
									} else {
										vErrors.push(err113);
									}
									errors++;
								}
							} else {
								const err114 = {
									instancePath:
										instancePath +
										"/front_matter/oncotype/absolute_benefit_percent",
									schemaPath:
										"#/properties/front_matter/properties/oncotype/properties/absolute_benefit_percent/type",
									keyword: "type",
									params: { type: "number" },
									message: "must be number",
								};
								if (vErrors === null) {
									vErrors = [err114];
								} else {
									vErrors.push(err114);
								}
								errors++;
							}
						}
					} else {
						const err115 = {
							instancePath: instancePath + "/front_matter/oncotype",
							schemaPath: "#/properties/front_matter/properties/oncotype/type",
							keyword: "type",
							params: { type: "object" },
							message: "must be object",
						};
						if (vErrors === null) {
							vErrors = [err115];
						} else {
							vErrors.push(err115);
						}
						errors++;
					}
				}
				if (data0.plan_structured !== undefined) {
					let data45 = data0.plan_structured;
					if (data45 && typeof data45 == "object" && !Array.isArray(data45)) {
						for (const key11 in data45) {
							if (
								!(
									key11 === "endocrine" ||
									key11 === "radiation_referred" ||
									key11 === "chemotherapy"
								)
							) {
								const err116 = {
									instancePath: instancePath + "/front_matter/plan_structured",
									schemaPath:
										"#/properties/front_matter/properties/plan_structured/additionalProperties",
									keyword: "additionalProperties",
									params: { additionalProperty: key11 },
									message: "must NOT have additional properties",
								};
								if (vErrors === null) {
									vErrors = [err116];
								} else {
									vErrors.push(err116);
								}
								errors++;
							}
						}
						if (data45.endocrine !== undefined) {
							let data46 = data45.endocrine;
							if (
								data46 &&
								typeof data46 == "object" &&
								!Array.isArray(data46)
							) {
								for (const key12 in data46) {
									if (
										!(
											key12 === "ordered" ||
											key12 === "agent" ||
											key12 === "agent_other"
										)
									) {
										const err117 = {
											instancePath:
												instancePath +
												"/front_matter/plan_structured/endocrine",
											schemaPath:
												"#/properties/front_matter/properties/plan_structured/properties/endocrine/additionalProperties",
											keyword: "additionalProperties",
											params: { additionalProperty: key12 },
											message: "must NOT have additional properties",
										};
										if (vErrors === null) {
											vErrors = [err117];
										} else {
											vErrors.push(err117);
										}
										errors++;
									}
								}
								if (data46.ordered !== undefined) {
									if (typeof data46.ordered !== "boolean") {
										const err118 = {
											instancePath:
												instancePath +
												"/front_matter/plan_structured/endocrine/ordered",
											schemaPath:
												"#/properties/front_matter/properties/plan_structured/properties/endocrine/properties/ordered/type",
											keyword: "type",
											params: { type: "boolean" },
											message: "must be boolean",
										};
										if (vErrors === null) {
											vErrors = [err118];
										} else {
											vErrors.push(err118);
										}
										errors++;
									}
								}
								if (data46.agent !== undefined) {
									let data48 = data46.agent;
									if (typeof data48 !== "string") {
										const err119 = {
											instancePath:
												instancePath +
												"/front_matter/plan_structured/endocrine/agent",
											schemaPath:
												"#/properties/front_matter/properties/plan_structured/properties/endocrine/properties/agent/type",
											keyword: "type",
											params: { type: "string" },
											message: "must be string",
										};
										if (vErrors === null) {
											vErrors = [err119];
										} else {
											vErrors.push(err119);
										}
										errors++;
									}
									if (
										!(
											data48 === "letrozole" ||
											data48 === "tamoxifen" ||
											data48 === "other"
										)
									) {
										const err120 = {
											instancePath:
												instancePath +
												"/front_matter/plan_structured/endocrine/agent",
											schemaPath:
												"#/properties/front_matter/properties/plan_structured/properties/endocrine/properties/agent/enum",
											keyword: "enum",
											params: {
												allowedValues:
													schema31.properties.front_matter.properties
														.plan_structured.properties.endocrine.properties
														.agent.enum,
											},
											message: "must be equal to one of the allowed values",
										};
										if (vErrors === null) {
											vErrors = [err120];
										} else {
											vErrors.push(err120);
										}
										errors++;
									}
								}
								if (data46.agent_other !== undefined) {
									let data49 = data46.agent_other;
									if (typeof data49 !== "string" && data49 !== null) {
										const err121 = {
											instancePath:
												instancePath +
												"/front_matter/plan_structured/endocrine/agent_other",
											schemaPath:
												"#/properties/front_matter/properties/plan_structured/properties/endocrine/properties/agent_other/type",
											keyword: "type",
											params: {
												type: schema31.properties.front_matter.properties
													.plan_structured.properties.endocrine.properties
													.agent_other.type,
											},
											message: "must be string,null",
										};
										if (vErrors === null) {
											vErrors = [err121];
										} else {
											vErrors.push(err121);
										}
										errors++;
									}
								}
							} else {
								const err122 = {
									instancePath:
										instancePath + "/front_matter/plan_structured/endocrine",
									schemaPath:
										"#/properties/front_matter/properties/plan_structured/properties/endocrine/type",
									keyword: "type",
									params: { type: "object" },
									message: "must be object",
								};
								if (vErrors === null) {
									vErrors = [err122];
								} else {
									vErrors.push(err122);
								}
								errors++;
							}
						}
						if (data45.radiation_referred !== undefined) {
							if (typeof data45.radiation_referred !== "boolean") {
								const err123 = {
									instancePath:
										instancePath +
										"/front_matter/plan_structured/radiation_referred",
									schemaPath:
										"#/properties/front_matter/properties/plan_structured/properties/radiation_referred/type",
									keyword: "type",
									params: { type: "boolean" },
									message: "must be boolean",
								};
								if (vErrors === null) {
									vErrors = [err123];
								} else {
									vErrors.push(err123);
								}
								errors++;
							}
						}
						if (data45.chemotherapy !== undefined) {
							let data51 = data45.chemotherapy;
							if (
								data51 &&
								typeof data51 == "object" &&
								!Array.isArray(data51)
							) {
								for (const key13 in data51) {
									if (
										!(
											key13 === "recommended" ||
											key13 === "risk_basis" ||
											key13 === "regimen" ||
											key13 === "regimen_other"
										)
									) {
										const err124 = {
											instancePath:
												instancePath +
												"/front_matter/plan_structured/chemotherapy",
											schemaPath:
												"#/properties/front_matter/properties/plan_structured/properties/chemotherapy/additionalProperties",
											keyword: "additionalProperties",
											params: { additionalProperty: key13 },
											message: "must NOT have additional properties",
										};
										if (vErrors === null) {
											vErrors = [err124];
										} else {
											vErrors.push(err124);
										}
										errors++;
									}
								}
								if (data51.recommended !== undefined) {
									if (typeof data51.recommended !== "boolean") {
										const err125 = {
											instancePath:
												instancePath +
												"/front_matter/plan_structured/chemotherapy/recommended",
											schemaPath:
												"#/properties/front_matter/properties/plan_structured/properties/chemotherapy/properties/recommended/type",
											keyword: "type",
											params: { type: "boolean" },
											message: "must be boolean",
										};
										if (vErrors === null) {
											vErrors = [err125];
										} else {
											vErrors.push(err125);
										}
										errors++;
									}
								}
								if (data51.risk_basis !== undefined) {
									let data53 = data51.risk_basis;
									if (typeof data53 !== "string") {
										const err126 = {
											instancePath:
												instancePath +
												"/front_matter/plan_structured/chemotherapy/risk_basis",
											schemaPath:
												"#/properties/front_matter/properties/plan_structured/properties/chemotherapy/properties/risk_basis/type",
											keyword: "type",
											params: { type: "string" },
											message: "must be string",
										};
										if (vErrors === null) {
											vErrors = [err126];
										} else {
											vErrors.push(err126);
										}
										errors++;
									}
									if (
										!(
											data53 === "low_oncotype" ||
											data53 === "high_oncotype" ||
											data53 === "high_risk_features" ||
											data53 === "her2_positive" ||
											data53 === "other"
										)
									) {
										const err127 = {
											instancePath:
												instancePath +
												"/front_matter/plan_structured/chemotherapy/risk_basis",
											schemaPath:
												"#/properties/front_matter/properties/plan_structured/properties/chemotherapy/properties/risk_basis/enum",
											keyword: "enum",
											params: {
												allowedValues:
													schema31.properties.front_matter.properties
														.plan_structured.properties.chemotherapy.properties
														.risk_basis.enum,
											},
											message: "must be equal to one of the allowed values",
										};
										if (vErrors === null) {
											vErrors = [err127];
										} else {
											vErrors.push(err127);
										}
										errors++;
									}
								}
								if (data51.regimen !== undefined) {
									let data54 = data51.regimen;
									if (typeof data54 !== "string" && data54 !== null) {
										const err128 = {
											instancePath:
												instancePath +
												"/front_matter/plan_structured/chemotherapy/regimen",
											schemaPath:
												"#/properties/front_matter/properties/plan_structured/properties/chemotherapy/properties/regimen/type",
											keyword: "type",
											params: {
												type: schema31.properties.front_matter.properties
													.plan_structured.properties.chemotherapy.properties
													.regimen.type,
											},
											message: "must be string,null",
										};
										if (vErrors === null) {
											vErrors = [err128];
										} else {
											vErrors.push(err128);
										}
										errors++;
									}
									if (
										!(
											data54 === "BRAJACTG" ||
											data54 === "BRAJDC" ||
											data54 === "AC_TH" ||
											data54 === "TCH" ||
											data54 === null
										)
									) {
										const err129 = {
											instancePath:
												instancePath +
												"/front_matter/plan_structured/chemotherapy/regimen",
											schemaPath:
												"#/properties/front_matter/properties/plan_structured/properties/chemotherapy/properties/regimen/enum",
											keyword: "enum",
											params: {
												allowedValues:
													schema31.properties.front_matter.properties
														.plan_structured.properties.chemotherapy.properties
														.regimen.enum,
											},
											message: "must be equal to one of the allowed values",
										};
										if (vErrors === null) {
											vErrors = [err129];
										} else {
											vErrors.push(err129);
										}
										errors++;
									}
								}
								if (data51.regimen_other !== undefined) {
									let data55 = data51.regimen_other;
									if (typeof data55 !== "string" && data55 !== null) {
										const err130 = {
											instancePath:
												instancePath +
												"/front_matter/plan_structured/chemotherapy/regimen_other",
											schemaPath:
												"#/properties/front_matter/properties/plan_structured/properties/chemotherapy/properties/regimen_other/type",
											keyword: "type",
											params: {
												type: schema31.properties.front_matter.properties
													.plan_structured.properties.chemotherapy.properties
													.regimen_other.type,
											},
											message: "must be string,null",
										};
										if (vErrors === null) {
											vErrors = [err130];
										} else {
											vErrors.push(err130);
										}
										errors++;
									}
								}
							} else {
								const err131 = {
									instancePath:
										instancePath + "/front_matter/plan_structured/chemotherapy",
									schemaPath:
										"#/properties/front_matter/properties/plan_structured/properties/chemotherapy/type",
									keyword: "type",
									params: { type: "object" },
									message: "must be object",
								};
								if (vErrors === null) {
									vErrors = [err131];
								} else {
									vErrors.push(err131);
								}
								errors++;
							}
						}
					} else {
						const err132 = {
							instancePath: instancePath + "/front_matter/plan_structured",
							schemaPath:
								"#/properties/front_matter/properties/plan_structured/type",
							keyword: "type",
							params: { type: "object" },
							message: "must be object",
						};
						if (vErrors === null) {
							vErrors = [err132];
						} else {
							vErrors.push(err132);
						}
						errors++;
					}
				}
				if (data0.medications !== undefined) {
					let data56 = data0.medications;
					if (Array.isArray(data56)) {
						const len1 = data56.length;
						for (let i1 = 0; i1 < len1; i1++) {
							let data57 = data56[i1];
							if (
								data57 &&
								typeof data57 == "object" &&
								!Array.isArray(data57)
							) {
								for (const key14 in data57) {
									if (
										!(
											key14 === "label" ||
											key14 === "dose" ||
											key14 === "dose_unit" ||
											key14 === "route" ||
											key14 === "frequency" ||
											key14 === "prn" ||
											key14 === "indication"
										)
									) {
										const err133 = {
											instancePath:
												instancePath + "/front_matter/medications/" + i1,
											schemaPath:
												"#/properties/front_matter/properties/medications/items/additionalProperties",
											keyword: "additionalProperties",
											params: { additionalProperty: key14 },
											message: "must NOT have additional properties",
										};
										if (vErrors === null) {
											vErrors = [err133];
										} else {
											vErrors.push(err133);
										}
										errors++;
									}
								}
								if (data57.label !== undefined) {
									if (typeof data57.label !== "string") {
										const err134 = {
											instancePath:
												instancePath +
												"/front_matter/medications/" +
												i1 +
												"/label",
											schemaPath:
												"#/properties/front_matter/properties/medications/items/properties/label/type",
											keyword: "type",
											params: { type: "string" },
											message: "must be string",
										};
										if (vErrors === null) {
											vErrors = [err134];
										} else {
											vErrors.push(err134);
										}
										errors++;
									}
								}
								if (data57.dose !== undefined) {
									let data59 = data57.dose;
									if (
										typeof data59 !== "string" &&
										!(typeof data59 == "number" && isFinite(data59))
									) {
										const err135 = {
											instancePath:
												instancePath +
												"/front_matter/medications/" +
												i1 +
												"/dose",
											schemaPath:
												"#/properties/front_matter/properties/medications/items/properties/dose/type",
											keyword: "type",
											params: {
												type: schema31.properties.front_matter.properties
													.medications.items.properties.dose.type,
											},
											message: "must be string,number",
										};
										if (vErrors === null) {
											vErrors = [err135];
										} else {
											vErrors.push(err135);
										}
										errors++;
									}
								}
								if (data57.dose_unit !== undefined) {
									if (typeof data57.dose_unit !== "string") {
										const err136 = {
											instancePath:
												instancePath +
												"/front_matter/medications/" +
												i1 +
												"/dose_unit",
											schemaPath:
												"#/properties/front_matter/properties/medications/items/properties/dose_unit/type",
											keyword: "type",
											params: { type: "string" },
											message: "must be string",
										};
										if (vErrors === null) {
											vErrors = [err136];
										} else {
											vErrors.push(err136);
										}
										errors++;
									}
								}
								if (data57.route !== undefined) {
									if (typeof data57.route !== "string") {
										const err137 = {
											instancePath:
												instancePath +
												"/front_matter/medications/" +
												i1 +
												"/route",
											schemaPath:
												"#/properties/front_matter/properties/medications/items/properties/route/type",
											keyword: "type",
											params: { type: "string" },
											message: "must be string",
										};
										if (vErrors === null) {
											vErrors = [err137];
										} else {
											vErrors.push(err137);
										}
										errors++;
									}
								}
								if (data57.frequency !== undefined) {
									if (typeof data57.frequency !== "string") {
										const err138 = {
											instancePath:
												instancePath +
												"/front_matter/medications/" +
												i1 +
												"/frequency",
											schemaPath:
												"#/properties/front_matter/properties/medications/items/properties/frequency/type",
											keyword: "type",
											params: { type: "string" },
											message: "must be string",
										};
										if (vErrors === null) {
											vErrors = [err138];
										} else {
											vErrors.push(err138);
										}
										errors++;
									}
								}
								if (data57.prn !== undefined) {
									if (typeof data57.prn !== "boolean") {
										const err139 = {
											instancePath:
												instancePath +
												"/front_matter/medications/" +
												i1 +
												"/prn",
											schemaPath:
												"#/properties/front_matter/properties/medications/items/properties/prn/type",
											keyword: "type",
											params: { type: "boolean" },
											message: "must be boolean",
										};
										if (vErrors === null) {
											vErrors = [err139];
										} else {
											vErrors.push(err139);
										}
										errors++;
									}
								}
								if (data57.indication !== undefined) {
									let data64 = data57.indication;
									if (typeof data64 !== "string" && data64 !== null) {
										const err140 = {
											instancePath:
												instancePath +
												"/front_matter/medications/" +
												i1 +
												"/indication",
											schemaPath:
												"#/properties/front_matter/properties/medications/items/properties/indication/type",
											keyword: "type",
											params: {
												type: schema31.properties.front_matter.properties
													.medications.items.properties.indication.type,
											},
											message: "must be string,null",
										};
										if (vErrors === null) {
											vErrors = [err140];
										} else {
											vErrors.push(err140);
										}
										errors++;
									}
								}
							} else {
								const err141 = {
									instancePath:
										instancePath + "/front_matter/medications/" + i1,
									schemaPath:
										"#/properties/front_matter/properties/medications/items/type",
									keyword: "type",
									params: { type: "object" },
									message: "must be object",
								};
								if (vErrors === null) {
									vErrors = [err141];
								} else {
									vErrors.push(err141);
								}
								errors++;
							}
						}
					} else {
						const err142 = {
							instancePath: instancePath + "/front_matter/medications",
							schemaPath:
								"#/properties/front_matter/properties/medications/type",
							keyword: "type",
							params: { type: "array" },
							message: "must be array",
						};
						if (vErrors === null) {
							vErrors = [err142];
						} else {
							vErrors.push(err142);
						}
						errors++;
					}
				}
				if (data0.allergies !== undefined) {
					let data65 = data0.allergies;
					if (Array.isArray(data65)) {
						const len2 = data65.length;
						for (let i2 = 0; i2 < len2; i2++) {
							let data66 = data65[i2];
							if (
								data66 &&
								typeof data66 == "object" &&
								!Array.isArray(data66)
							) {
								for (const key15 in data66) {
									if (
										!(
											key15 === "agent" ||
											key15 === "reaction" ||
											key15 === "severity"
										)
									) {
										const err143 = {
											instancePath:
												instancePath + "/front_matter/allergies/" + i2,
											schemaPath:
												"#/properties/front_matter/properties/allergies/items/additionalProperties",
											keyword: "additionalProperties",
											params: { additionalProperty: key15 },
											message: "must NOT have additional properties",
										};
										if (vErrors === null) {
											vErrors = [err143];
										} else {
											vErrors.push(err143);
										}
										errors++;
									}
								}
								if (data66.agent !== undefined) {
									if (typeof data66.agent !== "string") {
										const err144 = {
											instancePath:
												instancePath +
												"/front_matter/allergies/" +
												i2 +
												"/agent",
											schemaPath:
												"#/properties/front_matter/properties/allergies/items/properties/agent/type",
											keyword: "type",
											params: { type: "string" },
											message: "must be string",
										};
										if (vErrors === null) {
											vErrors = [err144];
										} else {
											vErrors.push(err144);
										}
										errors++;
									}
								}
								if (data66.reaction !== undefined) {
									if (typeof data66.reaction !== "string") {
										const err145 = {
											instancePath:
												instancePath +
												"/front_matter/allergies/" +
												i2 +
												"/reaction",
											schemaPath:
												"#/properties/front_matter/properties/allergies/items/properties/reaction/type",
											keyword: "type",
											params: { type: "string" },
											message: "must be string",
										};
										if (vErrors === null) {
											vErrors = [err145];
										} else {
											vErrors.push(err145);
										}
										errors++;
									}
								}
								if (data66.severity !== undefined) {
									if (typeof data66.severity !== "string") {
										const err146 = {
											instancePath:
												instancePath +
												"/front_matter/allergies/" +
												i2 +
												"/severity",
											schemaPath:
												"#/properties/front_matter/properties/allergies/items/properties/severity/type",
											keyword: "type",
											params: { type: "string" },
											message: "must be string",
										};
										if (vErrors === null) {
											vErrors = [err146];
										} else {
											vErrors.push(err146);
										}
										errors++;
									}
								}
							} else {
								const err147 = {
									instancePath: instancePath + "/front_matter/allergies/" + i2,
									schemaPath:
										"#/properties/front_matter/properties/allergies/items/type",
									keyword: "type",
									params: { type: "object" },
									message: "must be object",
								};
								if (vErrors === null) {
									vErrors = [err147];
								} else {
									vErrors.push(err147);
								}
								errors++;
							}
						}
					} else {
						const err148 = {
							instancePath: instancePath + "/front_matter/allergies",
							schemaPath: "#/properties/front_matter/properties/allergies/type",
							keyword: "type",
							params: { type: "array" },
							message: "must be array",
						};
						if (vErrors === null) {
							vErrors = [err148];
						} else {
							vErrors.push(err148);
						}
						errors++;
					}
				}
			} else {
				const err149 = {
					instancePath: instancePath + "/front_matter",
					schemaPath: "#/properties/front_matter/type",
					keyword: "type",
					params: { type: "object" },
					message: "must be object",
				};
				if (vErrors === null) {
					vErrors = [err149];
				} else {
					vErrors.push(err149);
				}
				errors++;
			}
		}
		if (data.content !== undefined) {
			let data70 = data.content;
			if (data70 && typeof data70 == "object" && !Array.isArray(data70)) {
				for (const key16 in data70) {
					if (!func1.call(schema31.properties.content.properties, key16)) {
						const err150 = {
							instancePath: instancePath + "/content",
							schemaPath: "#/properties/content/additionalProperties",
							keyword: "additionalProperties",
							params: { additionalProperty: key16 },
							message: "must NOT have additional properties",
						};
						if (vErrors === null) {
							vErrors = [err150];
						} else {
							vErrors.push(err150);
						}
						errors++;
					}
				}
				if (data70.reason !== undefined) {
					let data71 = data70.reason;
					if (typeof data71 !== "string" && data71 !== null) {
						const err151 = {
							instancePath: instancePath + "/content/reason",
							schemaPath: "#/properties/content/properties/reason/type",
							keyword: "type",
							params: {
								type: schema31.properties.content.properties.reason.type,
							},
							message: "must be string,null",
						};
						if (vErrors === null) {
							vErrors = [err151];
						} else {
							vErrors.push(err151);
						}
						errors++;
					}
				}
				if (data70.hpi !== undefined) {
					let data72 = data70.hpi;
					if (typeof data72 !== "string" && data72 !== null) {
						const err152 = {
							instancePath: instancePath + "/content/hpi",
							schemaPath: "#/properties/content/properties/hpi/type",
							keyword: "type",
							params: { type: schema31.properties.content.properties.hpi.type },
							message: "must be string,null",
						};
						if (vErrors === null) {
							vErrors = [err152];
						} else {
							vErrors.push(err152);
						}
						errors++;
					}
				}
				if (data70.pmh !== undefined) {
					let data73 = data70.pmh;
					if (typeof data73 !== "string" && data73 !== null) {
						const err153 = {
							instancePath: instancePath + "/content/pmh",
							schemaPath: "#/properties/content/properties/pmh/type",
							keyword: "type",
							params: { type: schema31.properties.content.properties.pmh.type },
							message: "must be string,null",
						};
						if (vErrors === null) {
							vErrors = [err153];
						} else {
							vErrors.push(err153);
						}
						errors++;
					}
				}
				if (data70.meds !== undefined) {
					let data74 = data70.meds;
					if (typeof data74 !== "string" && data74 !== null) {
						const err154 = {
							instancePath: instancePath + "/content/meds",
							schemaPath: "#/properties/content/properties/meds/type",
							keyword: "type",
							params: {
								type: schema31.properties.content.properties.meds.type,
							},
							message: "must be string,null",
						};
						if (vErrors === null) {
							vErrors = [err154];
						} else {
							vErrors.push(err154);
						}
						errors++;
					}
				}
				if (data70.allergies !== undefined) {
					let data75 = data70.allergies;
					if (typeof data75 !== "string" && data75 !== null) {
						const err155 = {
							instancePath: instancePath + "/content/allergies",
							schemaPath: "#/properties/content/properties/allergies/type",
							keyword: "type",
							params: {
								type: schema31.properties.content.properties.allergies.type,
							},
							message: "must be string,null",
						};
						if (vErrors === null) {
							vErrors = [err155];
						} else {
							vErrors.push(err155);
						}
						errors++;
					}
				}
				if (data70.social !== undefined) {
					let data76 = data70.social;
					if (typeof data76 !== "string" && data76 !== null) {
						const err156 = {
							instancePath: instancePath + "/content/social",
							schemaPath: "#/properties/content/properties/social/type",
							keyword: "type",
							params: {
								type: schema31.properties.content.properties.social.type,
							},
							message: "must be string,null",
						};
						if (vErrors === null) {
							vErrors = [err156];
						} else {
							vErrors.push(err156);
						}
						errors++;
					}
				}
				if (data70.family !== undefined) {
					let data77 = data70.family;
					if (typeof data77 !== "string" && data77 !== null) {
						const err157 = {
							instancePath: instancePath + "/content/family",
							schemaPath: "#/properties/content/properties/family/type",
							keyword: "type",
							params: {
								type: schema31.properties.content.properties.family.type,
							},
							message: "must be string,null",
						};
						if (vErrors === null) {
							vErrors = [err157];
						} else {
							vErrors.push(err157);
						}
						errors++;
					}
				}
				if (data70.exam !== undefined) {
					let data78 = data70.exam;
					if (typeof data78 !== "string" && data78 !== null) {
						const err158 = {
							instancePath: instancePath + "/content/exam",
							schemaPath: "#/properties/content/properties/exam/type",
							keyword: "type",
							params: {
								type: schema31.properties.content.properties.exam.type,
							},
							message: "must be string,null",
						};
						if (vErrors === null) {
							vErrors = [err158];
						} else {
							vErrors.push(err158);
						}
						errors++;
					}
				}
				if (data70.investigations !== undefined) {
					let data79 = data70.investigations;
					if (typeof data79 !== "string" && data79 !== null) {
						const err159 = {
							instancePath: instancePath + "/content/investigations",
							schemaPath: "#/properties/content/properties/investigations/type",
							keyword: "type",
							params: {
								type: schema31.properties.content.properties.investigations
									.type,
							},
							message: "must be string,null",
						};
						if (vErrors === null) {
							vErrors = [err159];
						} else {
							vErrors.push(err159);
						}
						errors++;
					}
				}
			} else {
				const err160 = {
					instancePath: instancePath + "/content",
					schemaPath: "#/properties/content/type",
					keyword: "type",
					params: { type: "object" },
					message: "must be object",
				};
				if (vErrors === null) {
					vErrors = [err160];
				} else {
					vErrors.push(err160);
				}
				errors++;
			}
		}
		if (data.extras !== undefined) {
			let data80 = data.extras;
			if (data80 && typeof data80 == "object" && !Array.isArray(data80)) {
				for (const key17 in data80) {
					if (!(key17 === "side")) {
						const err161 = {
							instancePath: instancePath + "/extras",
							schemaPath: "#/properties/extras/additionalProperties",
							keyword: "additionalProperties",
							params: { additionalProperty: key17 },
							message: "must NOT have additional properties",
						};
						if (vErrors === null) {
							vErrors = [err161];
						} else {
							vErrors.push(err161);
						}
						errors++;
					}
				}
				if (data80.side !== undefined) {
					let data81 = data80.side;
					if (typeof data81 !== "string") {
						const err162 = {
							instancePath: instancePath + "/extras/side",
							schemaPath: "#/properties/extras/properties/side/type",
							keyword: "type",
							params: { type: "string" },
							message: "must be string",
						};
						if (vErrors === null) {
							vErrors = [err162];
						} else {
							vErrors.push(err162);
						}
						errors++;
					}
					if (!(data81 === "left" || data81 === "right")) {
						const err163 = {
							instancePath: instancePath + "/extras/side",
							schemaPath: "#/properties/extras/properties/side/enum",
							keyword: "enum",
							params: {
								allowedValues: schema31.properties.extras.properties.side.enum,
							},
							message: "must be equal to one of the allowed values",
						};
						if (vErrors === null) {
							vErrors = [err163];
						} else {
							vErrors.push(err163);
						}
						errors++;
					}
				}
			} else {
				const err164 = {
					instancePath: instancePath + "/extras",
					schemaPath: "#/properties/extras/type",
					keyword: "type",
					params: { type: "object" },
					message: "must be object",
				};
				if (vErrors === null) {
					vErrors = [err164];
				} else {
					vErrors.push(err164);
				}
				errors++;
			}
		}
		if (data.flags !== undefined) {
			let data82 = data.flags;
			if (data82 && typeof data82 == "object" && !Array.isArray(data82)) {
				for (const key18 in data82) {
					if (!(key18 === "exam_present")) {
						const err165 = {
							instancePath: instancePath + "/flags",
							schemaPath: "#/properties/flags/additionalProperties",
							keyword: "additionalProperties",
							params: { additionalProperty: key18 },
							message: "must NOT have additional properties",
						};
						if (vErrors === null) {
							vErrors = [err165];
						} else {
							vErrors.push(err165);
						}
						errors++;
					}
				}
				if (data82.exam_present !== undefined) {
					if (typeof data82.exam_present !== "boolean") {
						const err166 = {
							instancePath: instancePath + "/flags/exam_present",
							schemaPath: "#/properties/flags/properties/exam_present/type",
							keyword: "type",
							params: { type: "boolean" },
							message: "must be boolean",
						};
						if (vErrors === null) {
							vErrors = [err166];
						} else {
							vErrors.push(err166);
						}
						errors++;
					}
				}
			} else {
				const err167 = {
					instancePath: instancePath + "/flags",
					schemaPath: "#/properties/flags/type",
					keyword: "type",
					params: { type: "object" },
					message: "must be object",
				};
				if (vErrors === null) {
					vErrors = [err167];
				} else {
					vErrors.push(err167);
				}
				errors++;
			}
		}
	} else {
		const err168 = {
			instancePath,
			schemaPath: "#/type",
			keyword: "type",
			params: { type: "object" },
			message: "must be object",
		};
		if (vErrors === null) {
			vErrors = [err168];
		} else {
			vErrors.push(err168);
		}
		errors++;
	}
	validate20.errors = vErrors;
	return errors === 0;
}
validate20.evaluated = {
	props: true,
	dynamicProps: false,
	dynamicItems: false,
};
