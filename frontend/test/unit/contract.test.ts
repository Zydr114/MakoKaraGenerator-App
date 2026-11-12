// 冻结合同的漂移检查：手写类型中的枚举／判别联合与冻结 OpenAPI 保持一致，
// 并用 examples.json 的正反例确认测试用的 schema 校验器本身可信。

import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import {
  BACKEND_ERROR_CODES,
  CONTRACT_PRODUCT_VERSION,
  CONTRACT_VERSION,
  JOB_KINDS,
  JOB_STATES,
  RENDER_BACKENDS,
  STYLE_PROPERTY_PATHS,
  TEMPLATE_PROPERTIES,
  TOKEN_TEMPLATE_PROPERTIES,
  COMPOSITE_TEMPLATE_PROPERTIES,
  generation,
  type LyricBranchRole,
  type LyricBranchTimingMode,
  type ProjectCommandType,
  type ProjectEditCommand,
  type PreviewRenderBackendInput,
  type RenderBackendsResponse,
  type RubyAlignment,
  type TimingStatus,
} from '../../src/protocol';
import { OPENAPI_PATH, OpenApiContract } from '../support/openapi';

const contract = OpenApiContract.load();
const contractDir = new URL('../../docs/backend/contract/', import.meta.url);
const manifest = JSON.parse(readFileSync(new URL('contract-manifest.json', contractDir), 'utf8')) as {
  contractVersion: string;
  productVersion: string;
  artifacts: Record<string, string>;
};

describe('冻结合同副本', () => {
  it('版本与 src/protocol 固定的版本一致', () => {
    expect(manifest.contractVersion).toBe(CONTRACT_VERSION);
    expect(manifest.productVersion).toBe(CONTRACT_PRODUCT_VERSION);
  });

  it('副本文件的 SHA-256 与 manifest 一致（防止手改副本）', () => {
    for (const [name, sha] of Object.entries(manifest.artifacts)) {
      const local = new URL(name.split('/').pop()!, contractDir);
      expect(createHash('sha256').update(readFileSync(local)).digest('hex'), name).toBe(sha);
    }
    expect(OPENAPI_PATH.endsWith('openapi.json')).toBe(true);
  });
});

describe('未唱辉光与样式属性路径合同（1.50.0）', () => {
  const glow = { color: { red: 80, green: 180, blue: 255, alpha: 255 }, opacity: 0.8, blurRadiusPx: 8, spreadPx: 2 };

  it('整体写入和关闭合法，缺字段、越界及额外字段被拒绝', () => {
    for (const value of [glow, null]) {
      expect(contract.validateNamed('StyleUnsungGlowChangeDtoInput', { property: 'unsung.glow', value })).toEqual([]);
    }
    for (const value of [
      { ...glow, opacity: 1.1 }, { ...glow, blurRadiusPx: -1 }, { ...glow, spreadPx: -1 },
      { ...glow, color: '#50B4FFFF' }, { ...glow, extra: 1 }, { color: glow.color },
    ]) {
      expect(contract.validateNamed('StyleUnsungGlowChangeDtoInput', { property: 'unsung.glow', value }).length).toBeGreaterThan(0);
    }
    expect(contract.validateNamed('StyleUnsungGlowChangeDto', { property: 'unsung.glow', value: null })).toEqual([]);
    expect(contract.validateNamed('StyleUnsungGlowChangeDto', { property: 'unsung.glow', value: glow }).length).toBeGreaterThan(0);
  });

  it('恢复整体或叶覆盖使用 restore，同批父叶并存与 project 被拒绝', () => {
    const command = { type: 'styles.restore', targets: [{ kind: 'branch', branchId: 'b' }], properties: ['unsung.glow'] };
    expect(contract.validateNamed('RestoreStylesCommandDtoInput', command)).toEqual([]);
    expect(contract.validateNamed('RestoreStylesCommandDtoInput', { ...command, properties: ['unsung.glow.blurRadiusPx'] })).toEqual([]);
    expect(contract.validateNamed('RestoreStylesCommandDtoInput', { ...command, properties: ['unsung.glow', 'unsung.glow.opacity'] }).length).toBeGreaterThan(0);
    expect(contract.validateNamed('RestoreStylesCommandDtoInput', { ...command, targets: [{ kind: 'project' }] }).length).toBeGreaterThan(0);
  });

  it('43 项属性路径及 token、composite 范围与冻结合同一致', () => {
    expect([...STYLE_PROPERTY_PATHS]).toHaveLength(43);
    expect([...STYLE_PROPERTY_PATHS].sort()).toEqual([...(contract.schema('StylePropertyPath').enum as string[])].sort());
    expect(TEMPLATE_PROPERTIES).toBe(STYLE_PROPERTY_PATHS);
    const rules = contract.schema('StaticTemplateDefinitionDto').allOf as { then: { properties: { properties: { items: { enum: string[] } } } } }[];
    expect([...TOKEN_TEMPLATE_PROPERTIES].sort()).toEqual([...rules[0]!.then.properties.properties.items.enum].sort());
    expect([...COMPOSITE_TEMPLATE_PROPERTIES].sort()).toEqual([...rules[1]!.then.properties.properties.items.enum].sort());
  });
});

/** oneOf 各分支的 type 常量。 */
function commandTypes(unionName: string): string[] {
  return (contract.schema(unionName).oneOf as { $ref: string }[]).map((b) => {
    const schema = contract.schema(b.$ref.split('/').pop()!);
    return (schema.properties as Record<string, { const: string }>).type!.const;
  });
}

describe('手写协议类型与 OpenAPI 一致', () => {
  it('BackendErrorCode 枚举完全一致', () => {
    expect([...BACKEND_ERROR_CODES].sort()).toEqual([...(contract.schema('BackendErrorCode').enum as string[])].sort());
  });

  it('JobKind／JobState 枚举完全一致', () => {
    const job = contract.schema('JobResponse').properties as Record<string, { enum: string[] }>;
    expect([...JOB_KINDS].sort()).toEqual([...job.kind!.enum].sort());
    expect([...JOB_STATES].sort()).toEqual([...job.state!.enum].sort());
  });

  it('96 种工程命令与 ProjectCommandDtoInput 的 oneOf 分支一致', () => {
    const types = commandTypes('ProjectCommandDtoInput');
    const expected: ProjectCommandType[] = [
      'lyrics.setTimeRange',
      'history.undo',
      'history.redo',
      'audio.analysis.apply',
      'audio.analysis.clear',
      'lyrics.stampTokenStart',
      'lyrics.stampTokenEnd',
      'lyrics.clearTokenTime',
      'lyrics.nudgeTokenTime',
      'lyrics.shiftLine',
      'lyrics.setTokenTimes',
      'lyrics.setLineText',
      'lyrics.setRubyText',
      'lyrics.addLine',
      'lyrics.duplicateLine',
      'lyrics.removeLine',
      'composition.setCanvasSize',
      'composition.setFrameRate',
      'composition.setBackgroundColor',
      'composition.setOutputDuration',
      'composition.useLyricsEnd',
      'composition.useAudioDuration',
      'lyrics.addTranslationBranch',
      'lyrics.configureBranch',
      'lyrics.removeBranch',
      'lyrics.linkPrimaryLine',
      'lyrics.splitToken',
      'lyrics.mergeToken',
      'lyrics.reorderToken',
      'lyrics.resplitLine',
      'lyrics.resplitBranch',
      'lyrics.importAsset',
      'lyrics.appendText',
      'styles.patch',
      'styles.restore',
      'presetApplications.apply',
      'presetApplications.update',
      'presetApplications.setEnabled',
      'presetApplications.remove',
      'orchestration.setDefaultContainer',
      'orchestration.createClip',
      'orchestration.renameClip',
      'orchestration.setClipContainer',
      'orchestration.deleteClip',
      'orchestration.previewBasicLayout',
      'orchestration.applyBasicLayout',
      'orchestration.setLayoutDefaultPlacement',
      'orchestration.setNodeGeometry',
      'orchestration.setLayoutSettings',
      'orchestration.setSlotAnimation',
      'orchestration.setProgramAnimationPolicy',
      'orchestration.createCountdown',
      'orchestration.createLeadIn',
      'orchestration.updateLeadIn',
      'orchestration.deleteLeadIn',
      'orchestration.setLineLocalTransform',
      'orchestration.createNode',
      'orchestration.copySubtree',
      'orchestration.setNode',
      'orchestration.reparent',
      'orchestration.reorderChildren',
      'orchestration.addSlot',
      'orchestration.removeNode',
      'orchestration.reorderSlots',
      'orchestration.setLayoutKind',
      'orchestration.setLineEnterAnimation',
      'orchestration.setProjectEnterAnimation',
      'orchestration.setLeadInGeneration',
      'orchestration.previewLeadInGeneration',
      'orchestration.applyLeadInGeneration',
      'orchestration.deleteLeadInAndExclude',
      'orchestration.setLeadInAttachment',
      'orchestration.addMark',
      'orchestration.setMarkText',
      'orchestration.reorderMarks',
      'orchestration.spaceMarks',
      'orchestration.setCountdownAppearance',
      'orchestration.setMarkAppearance',
      'audio.replaceAsset',
      'audio.clear',
      'composition.setBackgroundAsset',
      'composition.clearBackground',
      'composition.setBackgroundFit',
      'composition.setBackgroundPlayback',
      'project.applyEdits',
      'project.rename',
      'lyrics.setRubySegmentTime',
      'lyrics.clearRubySegmentTime',
      'lyrics.removeRubySegment',
      'lyrics.setRubySegmentText',
      'lyrics.addRubySegment',
      'lyrics.reorderRubySegments',
      'lyrics.setRubySegmentTimes',
      'orchestration.setClipMembers',
      'orchestration.splitClip',
      'orchestration.mergeClips',
    ];
    expect(types.sort()).toEqual(expected.sort());
  });

  it('project.applyEdits 能放的修改与 ProjectEditDtoInput 一致', () => {
    const excluded: Exclude<ProjectCommandType, ProjectEditCommand['type']>[] = [
      'history.undo',
      'history.redo',
      'audio.analysis.apply',
      'lyrics.importAsset',
      'audio.replaceAsset',
      'audio.clear',
      'composition.setBackgroundAsset',
      'project.applyEdits',
    ];
    const all = commandTypes('ProjectCommandDtoInput');
    expect(commandTypes('ProjectEditDtoInput').sort()).toEqual(all.filter((type) => !excluded.includes(type as never)).sort());
  });

  it('W1 读模型的枚举与 OpenAPI 一致', () => {
    const roles: LyricBranchRole[] = ['primary', 'translation', 'annotation', 'custom'];
    const modes: LyricBranchTimingMode[] = ['independent', 'followPrimary', 'none'];
    const statuses: TimingStatus[] = ['missing', 'imported', 'estimated', 'edited', 'confirmed', 'invalid'];
    const alignments: RubyAlignment[] = ['center', 'start', 'end'];
    expect([...(contract.schema('ProjectLyricBranchRole').enum as string[])].sort()).toEqual(roles.sort());
    expect([...(contract.schema('ProjectLyricTimingMode').enum as string[])].sort()).toEqual(modes.sort());
    expect([...(contract.schema('ProjectLyricTimingStatus').enum as string[])].sort()).toEqual(statuses.sort());
    expect([...(contract.schema('ProjectRubyAlignment').enum as string[])].sort()).toEqual(alignments.sort());
  });
});

describe('渲染设备（1.39.0）', () => {
  it('公开枚举只有 cpu、gpu', () => {
    const defaultBackend = (contract.schema('RenderBackendsResponse').properties as Record<string, { enum: string[] }>).defaultBackend!;
    expect([...defaultBackend.enum].sort()).toEqual([...RENDER_BACKENDS].sort());
  });

  it('手写类型的样例符合 schema', () => {
    const backends: RenderBackendsResponse = {
      defaultBackend: 'cpu',
      backends: [
        { renderBackend: 'cpu', available: true, api: 'raster', device: { device: 'Skia CPU raster' } },
        {
          renderBackend: 'gpu',
          available: false,
          api: 'vulkan',
          unavailableReason: { code: 'taskUnavailable', message: 'GPU device unavailable', field: 'renderBackend', failureCode: 'RenderBackendUnavailable', protocolVersion: '1' },
        },
      ],
    };
    expect(contract.validateNamed('RenderBackendsResponse', backends)).toEqual([]);
    const metadata = { requestId: 'preview-render-backend-1', hostInstanceId: 'host-A', validUntilUtc: '2026-10-27T00:00:00Z' };
    const input: PreviewRenderBackendInput = { expectedPreviewGeneration: generation('3'), renderBackend: 'gpu' };
    expect(contract.validateNamed('PreviewRenderBackendRequest', { ...input, metadata })).toEqual([]);
  });
});

describe('测试用 schema 校验器', () => {
  const examples = JSON.parse(readFileSync(new URL('examples.json', contractDir), 'utf8')) as {
    cases: { name: string; schema: string; valid: boolean; value: unknown }[];
  };

  it.each(examples.cases.map((c) => [c.name, c] as const))('%s', (_name, example) => {
    const issues = contract.validateNamed(example.schema, example.value);
    if (example.valid) expect(issues).toEqual([]);
    else expect(issues.length).toBeGreaterThan(0);
  });

  it('按实际路径找到操作并校验错误响应', () => {
    const result = contract.validateResponse('GET', '/api/v1/project-sessions/abc', 404, {
      code: 'sessionNotFound',
      message: '工程会话不存在。',
      field: 'sessionId',
      protocolVersion: '1',
    });
    expect(result?.template).toBe('/api/v1/project-sessions/{id}');
    expect(result?.issues).toEqual([]);
  });
});
