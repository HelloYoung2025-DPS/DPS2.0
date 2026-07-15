import base64
import json
import unittest
from pathlib import Path

from jsonschema import Draft202012Validator, FormatChecker


MODULE_ROOT = Path(__file__).resolve().parents[1]
SCHEMA_ROOT = MODULE_ROOT / "contracts" / "provided"
SCHEMA_FILES = tuple(sorted(SCHEMA_ROOT.glob("*.schema.json")))
ABSOLUTE_SHA256_PATTERN = r"^[a-f0-9]{64}$(?![\s\S])"
ABSOLUTE_VERSION_PATTERN = r"^1(?:\.[0-9]+){0,2}$(?![\s\S])"
ABSOLUTE_SIGNATURE_PATTERN = r"^[A-Za-z0-9+/]{86}==$(?![\s\S])"
UTC_SUFFIX_PATTERN = r"(?:Z|\+00:00)$(?![\s\S])"


def walk_property_schemas(node, path=()):
    if isinstance(node, dict):
        properties = node.get("properties", {})
        for name, child in properties.items():
            child_path = (*path, name)
            yield child_path, name, child
            yield from walk_property_schemas(child, child_path)
        if "items" in node:
            yield from walk_property_schemas(node["items"], (*path, "[]"))
        for keyword in ("allOf", "anyOf", "oneOf", "prefixItems"):
            for ordinal, child in enumerate(node.get(keyword, [])):
                yield from walk_property_schemas(child, (*path, keyword, str(ordinal)))


class CommandContractSchemaTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.schemas = {}
        for path in SCHEMA_FILES:
            schema = json.loads(path.read_text(encoding="utf-8"))
            Draft202012Validator.check_schema(schema)
            cls.schemas[path.name] = schema
        cls.properties = [
            (schema_name, *entry)
            for schema_name, schema in cls.schemas.items()
            for entry in walk_property_schemas(schema)
        ]
        cls.valid_signature = base64.b64encode(bytes(range(64))).decode("ascii")

    @staticmethod
    def validator(property_schema):
        return Draft202012Validator(
            {
                "$schema": "https://json-schema.org/draft/2020-12/schema",
                **property_schema,
            },
            format_checker=FormatChecker(),
        )

    def test_every_sha256_property_has_exact_length_and_absolute_end(self):
        sha_properties = [
            (schema_name, path, name, schema)
            for schema_name, path, name, schema in self.properties
            if name.endswith("_sha256") or name == "evidence_digest"
        ]
        self.assertEqual(12, len(sha_properties))
        valid = "a" * 64
        invalid_values = (
            "",
            "a" * 63,
            "a" * 65,
            "A" * 64,
            valid + "\n",
            valid + "\r",
            valid + " ",
        )
        for schema_name, path, name, property_schema in sha_properties:
            with self.subTest(schema=schema_name, path=".".join(path), property=name):
                self.assertEqual(64, property_schema.get("minLength"))
                self.assertEqual(64, property_schema.get("maxLength"))
                self.assertEqual(ABSOLUTE_SHA256_PATTERN, property_schema.get("pattern"))
                validator = self.validator(property_schema)
                self.assertTrue(validator.is_valid(valid))
                for value in invalid_values:
                    self.assertFalse(validator.is_valid(value), repr(value))
                if "null" in property_schema.get("type", []):
                    self.assertTrue(validator.is_valid(None))

    def test_every_schema_version_has_an_absolute_end(self):
        version_properties = [
            (schema_name, path, schema)
            for schema_name, path, name, schema in self.properties
            if name == "schema_version"
        ]
        self.assertEqual(4, len(version_properties))
        for schema_name, path, property_schema in version_properties:
            with self.subTest(schema=schema_name, path=".".join(path)):
                self.assertEqual(ABSOLUTE_VERSION_PATTERN, property_schema.get("pattern"))
                validator = self.validator(property_schema)
                for value in ("1", "1.0", "1.0.0"):
                    self.assertTrue(validator.is_valid(value))
                for value in ("", "2.0.0", "1.0.0\n", "1.0.0\r", "1.0.0 ", "1.0.0.0"):
                    self.assertFalse(validator.is_valid(value), repr(value))

    def test_exact_64_byte_p1363_base64_signatures_are_canonical(self):
        signature_properties = [
            (schema_name, path, schema)
            for schema_name, path, name, schema in self.properties
            if name == "signature_base64"
        ]
        self.assertEqual(2, len(signature_properties))
        invalid_values = (
            "",
            "AA==",
            "not-base64",
            base64.b64encode(bytes(range(63))).decode("ascii"),
            base64.b64encode(bytes(range(65))).decode("ascii"),
            self.valid_signature + "\n",
            self.valid_signature + "\r",
            self.valid_signature + " ",
            self.valid_signature.rstrip("="),
            self.valid_signature.replace("+", "-"),
        )
        for schema_name, path, property_schema in signature_properties:
            with self.subTest(schema=schema_name, path=".".join(path)):
                self.assertEqual(88, property_schema.get("minLength"))
                self.assertEqual(88, property_schema.get("maxLength"))
                self.assertEqual(ABSOLUTE_SIGNATURE_PATTERN, property_schema.get("pattern"))
                validator = self.validator(property_schema)
                self.assertTrue(validator.is_valid(self.valid_signature))
                for value in invalid_values:
                    self.assertFalse(validator.is_valid(value), repr(value))

    def test_date_time_fields_accept_only_explicit_zero_offset_wire_values(self):
        time_properties = [
            (schema_name, path, schema)
            for schema_name, path, _name, schema in self.properties
            if schema.get("format") == "date-time"
        ]
        self.assertEqual(6, len(time_properties))
        for schema_name, path, property_schema in time_properties:
            with self.subTest(schema=schema_name, path=".".join(path)):
                self.assertEqual(UTC_SUFFIX_PATTERN, property_schema.get("pattern"))
                validator = self.validator(property_schema)
                self.assertTrue(validator.is_valid("2026-01-01T00:00:00Z"))
                self.assertTrue(validator.is_valid("2026-01-01T00:00:00+00:00"))
                for value in (
                    "2026-01-01T08:00:00+08:00",
                    "2026-01-01T00:00:00-00:00",
                    "2026-01-01T00:00:00Z\n",
                ):
                    self.assertFalse(validator.is_valid(value), repr(value))


if __name__ == "__main__":
    unittest.main()
