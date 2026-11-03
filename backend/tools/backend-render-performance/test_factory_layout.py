import shutil
import tempfile
import unittest
import contextlib
import io
import json
from types import SimpleNamespace
from unittest import mock
from pathlib import Path

import bench
import factory_layout as contract


class FactoryLayoutTests(unittest.TestCase):
    def test_exact_contracts_and_invalid_layouts(self):
        old = contract.OLD_SIGNATURE + "\n" + contract.OLD_CALL
        new = contract.NEW_SIGNATURE + "\n" + contract.NEW_CALL
        self.assertEqual(contract.detect(old), "legacy")
        self.assertEqual(contract.detect(new), "injected")
        for source in ("", old + new, old + contract.OLD_SIGNATURE,
                       contract.OLD_SIGNATURE + contract.NEW_CALL,
                       new.replace("IScriptCompilationService", "UnknownCompiler")):
            with self.subTest(source=source), self.assertRaises(ValueError):
                contract.detect(source)

    def test_isolated_adaptation_and_inventory(self):
        for layout, signature, call in (("legacy", contract.OLD_SIGNATURE, contract.OLD_CALL),
                                        ("injected", contract.NEW_SIGNATURE, contract.NEW_CALL)):
            with self.subTest(layout=layout), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                factory = root / contract.FACTORY
                factory.parent.mkdir(parents=True)
                tool = root / "tools/backend-render-performance"
                shutil.copytree(bench.TOOL, tool, ignore=shutil.ignore_patterns("bin", "obj", "__pycache__"))
                template = bench.inventory(tool)
                factory.write_text(signature + "\n" + call)
                before = factory.read_bytes()
                self.assertEqual(contract.adapt(root), layout)
                self.assertEqual(factory.read_bytes(), before)
                changes = bench.tool_changes(template, bench.inventory(tool))
                expected = {"Runner/Program.cs", "Runner/Runner.csproj"} if layout == "legacy" else set()
                self.assertEqual(set(changes), expected)
                xml = (tool / "Runner/Runner.csproj").read_text()
                text = (tool / "Runner/Program.cs").read_text()
                self.assertEqual(contract.COMPILER_REFERENCE in xml, layout == "injected")
                self.assertEqual(contract.COMPILER_USING in text, layout == "injected")
                self.assertIn(contract.NEW_RUNNER if layout == "injected" else contract.OLD_RUNNER, text)
                # Both layouts include construction in the same create timer/allocation boundary.
                start = text.index("var stopwatch = Stopwatch.StartNew();")
                factory_start = text.index("var factory = new ProjectRenderSessionFactory(")
                create = text.index("var session = await factory.CreateAsync(fixture.Snapshot);")
                self.assertLess(start, factory_start)
                self.assertLess(factory_start, create)
                if layout == "legacy":
                    with self.assertRaises(ValueError):
                        contract.adapt(root)

    def test_single_prepare_records_generated_tools_for_archive_compatibility(self):
        def extract(commit, destination):
            factory = destination / contract.FACTORY
            factory.parent.mkdir(parents=True)
            factory.write_text(contract.OLD_SIGNATURE + "\n" + contract.OLD_CALL)
            return "backend/"
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "prepared"
            with mock.patch.object(bench, "git", return_value="fixed-commit"), \
                 mock.patch.object(bench, "extract_source", side_effect=extract), \
                 mock.patch.object(bench.subprocess, "run"), contextlib.redirect_stdout(io.StringIO()):
                bench.prepare(SimpleNamespace(output=str(root), commit="fixed-commit"))
            metadata = json.loads((root / "prepare.json").read_text())
            generated = bench.inventory(root / "sources/baseline/tools/backend-render-performance")
            self.assertEqual(metadata["toolInputs"], generated)
            self.assertEqual(metadata["toolTemplateInputs"], bench.inventory(bench.TOOL))
            self.assertEqual(metadata["factoryLayout"], "legacy")
            self.assertEqual(set(bench.tool_changes(metadata["toolTemplateInputs"], generated)),
                             {"Runner/Program.cs", "Runner/Runner.csproj"})

    def test_unknown_template_is_rejected_before_any_write(self):
        for mutation in (lambda s: s.replace(contract.NEW_RUNNER, "unknown runner"),
                         lambda s: s.replace(contract.COMPILER_USING, ""),
                         lambda s: s + "\n" + contract.NEW_RUNNER):
            with tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                factory = root / contract.FACTORY
                factory.parent.mkdir(parents=True)
                factory.write_text(contract.OLD_SIGNATURE + "\n" + contract.OLD_CALL)
                tool = root / "tools/backend-render-performance"
                shutil.copytree(bench.TOOL, tool, ignore=shutil.ignore_patterns("bin", "obj", "__pycache__"))
                runner = tool / "Runner/Program.cs"
                runner.write_text(mutation(runner.read_text()))
                before = bench.inventory(tool)
                with self.assertRaises(ValueError):
                    contract.adapt(root)
                self.assertEqual(before, bench.inventory(tool))


if __name__ == "__main__":
    unittest.main()
