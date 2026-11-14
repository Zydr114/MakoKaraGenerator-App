"""Verify strict old/new tracing anchors without building or rendering."""

import unittest

import instrument


DEVICE_PREVIEW = '''            device = SkiaRenderDeviceFactory.Create(enableGpuPreview
                ? (_renderBackendOptions ?? SkiaRenderBackendOptions.FromEnvironment()) with { EnableGpuPreview = true }
                : _renderBackendOptions);'''
DEVICE_PREPARED = '''bool enableGpuPreviewBenchmark = false, RuntimeGpuPreparation? gpuPreparation = null)
            if (gpuPreparation is not null && (!enableGpuPreview ||
                (_renderBackendOptions ?? SkiaRenderBackendOptions.FromEnvironment()).Backend != SkiaRenderBackend.Vulkan))
                throw new InvalidOperationException("GPU 预热只能转移给显式 Vulkan shared preview 会话。");
            device = gpuPreparation?.TakeDevice() ?? SkiaRenderDeviceFactory.Create(enableGpuPreview
                ? (_renderBackendOptions ?? SkiaRenderBackendOptions.FromEnvironment()) with { EnableGpuPreview = true }
                : _renderBackendOptions);'''
DEVICE_PREPARED_CURRENT = DEVICE_PREPARED.replace(
    'RuntimeGpuPreparation? gpuPreparation = null)',
    'RuntimeGpuPreparation? gpuPreparation = null,\n'
    '    ProjectMediaFeatureBinding? mediaBinding = null, bool graphPixelChecks = true)')
MAIN_EXPLICIT = '''        using var scratch = state.Resources.Reserve(1, bytes, "SceneBackdropSnapshot");
        _device.FlushAndSubmit(main);
        using var snapshot = main.Snapshot();'''
MAIN_REMOVED = '''        using var scratch = state.Resources.Reserve(1, bytes, "SceneBackdropSnapshot");
        using var snapshot = main.Snapshot();'''
MAIN_REMOVED_CHARGED = '''        using var scratch = state.Resources.Reserve(1, bytes, "SceneBackdropSnapshot");
        state.Resources.ChargeScenePass(request.CancellationToken);
        using var snapshot = main.Snapshot();'''
VIDEO_OLD = '''        private readonly byte[] _buffer;
        private readonly VideoFrame _frame;
        private async Task<VideoFrame?> GetFrameAsync(long sourceTimeUs, CancellationToken caller)
                        int read = await _process!.Output.ReadAsync(_buffer.AsMemory(total), deadline.Token).ConfigureAwait(false);
                        deadline.ThrowIfExpired();'''
VIDEO_NEW = '''        private byte[] _buffer;
        private VideoFrame _frame;
        private async Task<VideoFrame?> GetFrameAsync(long sourceTimeUs, int width, int height, CancellationToken caller)
                        int read = await _process!.Output.ReadAsync(_buffer.AsMemory(total), deadline.Token).ConfigureAwait(false);
                        deadline.ThrowIfExpired();'''


class InstrumentLayoutTests(unittest.TestCase):
    def test_legacy_preview_and_prepared_device_contracts(self):
        for source, expected in (
                ('            device = SkiaRenderDeviceFactory.Create(_renderBackendOptions);', 'legacy'),
                (DEVICE_PREVIEW, 'preview'), (DEVICE_PREPARED, 'prepared-preview'),
                (DEVICE_PREPARED_CURRENT, 'prepared-preview')):
            with self.subTest(layout=expected):
                layout, statement = instrument.device_factory_layout(source)
                self.assertEqual(layout, expected)
                self.assertEqual(source.count(statement), 1)

    def test_device_contract_rejects_unknown_duplicate_and_mixed_sources(self):
        for source in ('', DEVICE_PREVIEW * 2, DEVICE_PREVIEW + DEVICE_PREPARED,
                       DEVICE_PREPARED.replace('TakeDevice()', 'OtherDevice()'),
                       DEVICE_PREPARED.replace('RuntimeGpuPreparation?', 'OtherPreparation?'),
                       DEVICE_PREPARED.replace('SkiaRenderBackend.Vulkan', 'SkiaRenderBackend.Cpu'),
                       DEVICE_PREPARED.replace('gpuPreparation?.TakeDevice() ?? ', '')):
            with self.subTest(source=source), self.assertRaises(ValueError):
                instrument.device_factory_layout(source)

    def test_main_submit_explicit_and_removed_contracts(self):
        self.assertEqual(instrument.scene_main_submit_layout(MAIN_EXPLICIT), ('explicit', MAIN_EXPLICIT))
        self.assertEqual(instrument.scene_main_submit_layout(MAIN_REMOVED), ('removed', MAIN_REMOVED))
        self.assertEqual(instrument.scene_main_submit_layout(MAIN_REMOVED_CHARGED), ('removed', MAIN_REMOVED_CHARGED))

    def test_main_submit_missing_unknown_or_duplicate_never_means_removed(self):
        for source in ('', MAIN_EXPLICIT * 2, MAIN_REMOVED * 2, MAIN_EXPLICIT + MAIN_REMOVED,
                       MAIN_REMOVED + '\n        _device.FlushAndSubmit(main);',
                       MAIN_EXPLICIT.replace('_device.FlushAndSubmit(main);', '_device.FlushAndSubmit(other);'),
                       MAIN_REMOVED.replace('main.Snapshot()', 'other.Snapshot()')):
            with self.subTest(source=source), self.assertRaises(ValueError):
                instrument.scene_main_submit_layout(source)

    def test_video_full_size_and_target_size_contracts(self):
        for source, expected in ((VIDEO_OLD, 'full-size'), (VIDEO_NEW, 'target-size')):
            with self.subTest(layout=expected):
                layout, read = instrument.video_stdout_layout(source)
                self.assertEqual(layout, expected)
                self.assertEqual(source.count(read), 1)

    def test_video_missing_mixed_and_duplicate_reads_are_rejected(self):
        for source in ('', VIDEO_OLD + VIDEO_NEW, VIDEO_NEW * 2,
                       VIDEO_NEW.replace('private byte[]', 'private readonly byte[]'),
                       VIDEO_NEW.replace('Output.ReadAsync', 'OtherOutput.ReadAsync'),
                       VIDEO_OLD.replace('deadline.ThrowIfExpired();', '')):
            with self.subTest(source=source), self.assertRaises(ValueError):
                instrument.video_stdout_layout(source)


if __name__ == '__main__':
    unittest.main()
