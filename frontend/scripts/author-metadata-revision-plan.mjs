// Pure fixture planning only. The input HTTP archive is never rewritten.
export const COUNTER = 'metadataCounter';
export const HOLD = 'metadataHoldUs';
export const COUNTER_LABEL = '修订计数';
export const HOLD_LABEL = '修订停留';
export const MAX_I64 = '9223372036854775807';
export const ABOVE_JS_INTEGER = '9007199254740993';
export const MAX_U64 = '18446744073709551615';
export const TARGET_EXPORT_LABEL = '迁移绘图';

export function makeRevisionPlan(fixture, canonicalManifest) {
  if (canonicalManifest.parameters?.length !== 0 || canonicalManifest.exports?.length !== 1
    || canonicalManifest.exports[0].kind !== 'controller')
    throw new Error('此有限场景要求无参数、单 controller 的本轮 HTTP vector fixture；不能静默改造其他素材。');
  const manifest = structuredClone(canonicalManifest);
  const original = manifest.exports[0];
  const exportId = original.id + '.metadata';
  const markers = { default: '__MKG_I64_DEFAULT__', max: '__MKG_I64_MAX__' };
  manifest.name += ' metadata migration';
  manifest.version = '2.0.1';
  manifest.parameters = [
    { id: COUNTER, type: 'integer', label: COUNTER_LABEL, default: markers.default, min: 0, max: markers.max },
    { id: HOLD, type: 'duration', label: HOLD_LABEL, default: 100000, min: 0, max: 2000000, unit: 'us' },
  ];
  // Entry type and declared visual output identity remain those of the real fixture.
  manifest.exports = [{ ...original, id: exportId, name: TARGET_EXPORT_LABEL, parameters: [COUNTER, HOLD] }];
  const manifestText = JSON.stringify(manifest, null, 2)
    .replace(JSON.stringify(markers.default), ABOVE_JS_INTEGER)
    .replace(JSON.stringify(markers.max), MAX_I64) + '\n';
  if (!manifestText.includes(`"default": ${ABOVE_JS_INTEGER}`) || !manifestText.includes(`"max": ${MAX_I64}`))
    throw new Error('Int64 fixture text markers were not replaced exactly.');
  return { exportId, manifestText, svgText: fixture.repairedSvg, originalExportId: original.id,
    originalExportLabel: original.name, colorA: fixture.initialColor, colorB: fixture.repairedColor };
}
