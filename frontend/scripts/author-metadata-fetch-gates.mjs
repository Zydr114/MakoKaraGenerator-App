// Test-only delivery controls. Every matched request still reaches native fetch.
// Delay gates return the same real Response later. The outage drops exactly four
// GET deliveries in one logical read (client's initial try + three read retries),
// after observing an actual successful adopt POST. No response body is invented.
export class BrowserFetchGates {
  constructor(page) { this.page = page; }

  async install() {
    await this.page.eval(`(() => {
      if (globalThis.__makoMetadataSmoke) throw new Error('Smoke delivery gates already installed.');
      const nativeFetch = globalThis.fetch;
      const state = { events: [], pending: new Map(), delay: null, outage: null, sequence: 0 };
      const event = (kind, fields = {}) => state.events.push({ sequence: ++state.sequence, kind, ...fields });
      const fields = (url) => ({ path: url.pathname,
        expectedDraftRevision: url.searchParams.get('expectedDraftRevision'), sourceRevision: url.searchParams.get('sourceRevision') });
      const wrapper = async (input, init) => {
        const url = new URL(input instanceof Request ? input.url : String(input), location.href);
        const method = String(init?.method ?? (input instanceof Request ? input.method : 'GET')).toUpperCase();
        const delay = method === 'GET' && url.pathname.endsWith('/source-inspection') && state.delay?.remaining > 0
          && url.searchParams.get('sourceRevision') === state.delay.sourceRevision ? state.delay : null;
        if (delay) delay.remaining--;
        const response = await nativeFetch.call(globalThis, input, init);
        if (method === 'POST' && state.outage?.armed === false && url.pathname === state.outage.adoptPath && response.ok) {
          const receipt = await response.clone().json();
          state.outage.armed = true;
          event('real-adopt-succeeded-before-outage', { path: url.pathname, status: response.status,
            draftId: receipt.draftId, draftRevision: receipt.draftRevision, adoptedSourceRevision: receipt.adoptedSourceRevision });
        }
        if (delay) {
          // Fully consume a clone so CDP records actual bytes while UI delivery is held.
          const observed = await response.clone().json();
          const id = 'metadata-' + (state.sequence + 1);
          event('real-metadata-held', { id, ...fields(url), status: response.status,
            responseDraftRevision: observed.draftRevision, responseSourceRevision: observed.sourceRevision });
          await new Promise((release) => state.pending.set(id, release));
          state.pending.delete(id);
          event('real-metadata-released-unchanged', { id, ...fields(url), status: response.status });
        }
        const outage = state.outage;
        if (method === 'GET' && outage?.armed && outage.remaining > 0 && url.pathname === outage.draftPath) {
          await response.clone().arrayBuffer();
          outage.remaining--;
          event('injected-post-adopt-get-delivery-failure', { path: url.pathname, realStatus: response.status,
            remaining: outage.remaining, simulation: 'TypeError after real native response; one logical GET with read retries' });
          throw new TypeError('Smoke injected post-adopt GET delivery failure');
        }
        return response;
      };
      state.restore = () => { for (const release of state.pending.values()) release(); state.pending.clear();
        if (globalThis.fetch === wrapper) globalThis.fetch = nativeFetch; };
      globalThis.__makoMetadataSmoke = state;
      globalThis.fetch = wrapper;
    })()`);
  }

  async delayNextMetadata(sourceRevision) {
    await this.page.eval(`globalThis.__makoMetadataSmoke.delay = { sourceRevision: ${JSON.stringify(sourceRevision)}, remaining: 1 }`);
  }

  async waitHeld() {
    return this.page.waitFor(`(() => { const s = globalThis.__makoMetadataSmoke;
      const e = s.events.find((event) => event.kind === 'real-metadata-held' && s.pending.has(event.id)); return e ?? null; })()`,
    '延迟交付的真实 metadata 响应');
  }

  async release(id) {
    await this.page.eval(`(() => { const release = globalThis.__makoMetadataSmoke.pending.get(${JSON.stringify(id)});
      if (!release) throw new Error('No pending real metadata response'); release(); })()`);
  }

  async failPostAdoptRead(draftId) {
    const draftPath = `/api/v1/drafts/${draftId}`;
    await this.page.eval(`globalThis.__makoMetadataSmoke.outage = { draftPath: ${JSON.stringify(draftPath)},
      adoptPath: ${JSON.stringify(draftPath + '/adopt-source-revision')}, armed: false, remaining: 4 }`);
  }

  async waitOutageConsumed() {
    return this.page.waitFor(`(() => { const s = globalThis.__makoMetadataSmoke;
      return s.outage?.armed && s.outage.remaining === 0 ? s.events.filter((event) => event.kind === 'injected-post-adopt-get-delivery-failure') : null; })()`,
    '单次逻辑读取的四次交付故障已消耗');
  }

  async snapshot() { return this.page.eval('globalThis.__makoMetadataSmoke?.events ?? []'); }
  async restore() {
    return this.page.eval(`(() => { const s = globalThis.__makoMetadataSmoke; if (!s) return [];
      const events = s.events; s.restore(); delete globalThis.__makoMetadataSmoke; return events; })()`);
  }
}
