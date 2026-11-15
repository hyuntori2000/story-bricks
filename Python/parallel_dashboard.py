"""Add the parallel recognition experiment log to the existing capture dashboard."""

_MARKER = 'id="parallel-recognition-history"'

_SECTION = r'''
<section class="panel" id="parallel-recognition-history" aria-labelledby="parallel-history-title">
  <h2 id="parallel-history-title">Recognition experiment history</h2>
  <p>OCR and braille read the same capture. The first accepted whole word wins; both engines continue to finish.
  Times are local elapsed milliseconds, not accuracy scores. The latest 200 captures appear here; the complete history is retained on disk.</p>
  <p id="parallel-history-status" role="status">Waiting for recognition history.</p>
  <div class="parallel-history-scroll">
    <table><thead><tr><th scope="col">Capture / UTC time</th><th scope="col">Chosen word / source / decision</th>
      <th scope="col">OCR result</th><th scope="col">Braille result</th><th scope="col">Comparison</th></tr></thead>
      <tbody id="parallel-history-rows"><tr><td colspan="5">No experiment captures yet.</td></tr></tbody>
    </table>
  </div>
</section>
<style>
#parallel-recognition-history p{font-size:14px}
#parallel-recognition-history .parallel-history-scroll{overflow:auto;max-height:620px}
#parallel-recognition-history table{border-collapse:collapse;width:100%;font-size:13px;min-width:850px;text-align:left}
#parallel-recognition-history th,#parallel-recognition-history td{padding:10px;border:1px solid #48606d;vertical-align:top;overflow-wrap:anywhere}
#parallel-recognition-history th{background:#203543;position:sticky;top:0}
#parallel-recognition-history td>div+div{margin-top:5px}
#parallel-recognition-history .parallel-detail{color:#c4d0da;font-size:12px}
#parallel-recognition-history .parallel-disagree{color:#ffca82}
#parallel-recognition-history .parallel-failed{color:#ffb9b9}
</style>
'''

_SCRIPT = r'''
<script>
(() => {
  'use strict';
  function historyUrl(){const url=new URL('/recognition-history.json',location.origin),scope=new URLSearchParams(location.search);for(const key of ['workspaceId','experienceSessionId'])if(scope.has(key))url.searchParams.set(key,scope.get(key));return url.pathname+url.search;}
  const rows = document.getElementById('parallel-history-rows');
  const status = document.getElementById('parallel-history-status');
  if (!rows || !status) return;
  let previous = '', failures = 0;
  const printable = value => typeof value === 'string' || typeof value === 'number' ? String(value) : '';
  const duration = value => typeof value === 'number' && Number.isFinite(value) && value >= 0 ? value.toFixed(0) + ' ms' : '—';
  const accepted = engine => !!engine && engine.status === 'recognized' && !!printable(engine.word);
  function line(cell, value, detail = false) {
    const element = document.createElement('div');
    element.textContent = value;
    if (detail) element.className = 'parallel-detail';
    cell.appendChild(element);
  }
  function engineCell(record, name) {
    const cell = document.createElement('td');
    const engine = record[name] && typeof record[name] === 'object' ? record[name] : {};
    const engineStatus = printable(engine.status);
    const running = !record.complete && (!engineStatus || ['pending', 'queued', 'running', 'not_started'].includes(engineStatus));
    line(cell, printable(engine.word) || (running ? 'Running…' : 'No accepted word'));
    line(cell, (running ? 'running' : engineStatus || 'unavailable') + ' · ' + duration(engine.elapsedMs), true);
    const reason = printable(engine.reason);
    if (engine.readerPath) line(cell, printable(engine.readerPath), true);
    if (engine.geometryIssue) line(cell, 'Geometry: ' + printable(engine.geometryIssue), true);
    if (reason) line(cell, reason, true);
    return cell;
  }
  function render(records) {
    const fragment = document.createDocumentFragment();
    if (!records.length) {
      const row = document.createElement('tr'), cell = document.createElement('td');
      cell.colSpan = 5; cell.textContent = 'No experiment captures yet.';
      row.appendChild(cell); fragment.appendChild(row);
    }
    for (const record of records) {
      const row = document.createElement('tr'), capture = document.createElement('td');
      line(capture, printable(record.startedAtUtc) || 'Time unavailable');
      line(capture, 'Frame: ' + (printable(record.frameId) || '—'), true);
      line(capture, 'Session: ' + (printable(record.sessionId) || '—'), true);
      row.appendChild(capture);
      const chosen = document.createElement('td');
      const word = printable(record.selectedWord);
      line(chosen, word || (record.complete ? 'No accepted word' : 'Waiting for first accepted word…'));
      line(chosen, (printable(record.selectedSource) || 'No winner') + ' · ' + duration(record.decisionMs), true);
      row.append(chosen, engineCell(record, 'ocr'), engineCell(record, 'braille'));
      const comparison = document.createElement('td');
      if (record.disagreement === true) {
        comparison.textContent = 'Different words'; comparison.className = 'parallel-disagree';
      } else if (!record.complete) comparison.textContent = 'Waiting for both engines';
      else if (!word) { comparison.textContent = 'Both failed'; comparison.className = 'parallel-failed'; }
      else if (accepted(record.ocr) && accepted(record.braille)) comparison.textContent = 'Same word';
      else comparison.textContent = 'One engine accepted';
      row.appendChild(comparison); fragment.appendChild(row);
    }
    rows.replaceChildren(fragment);
  }
  async function pollHistory() {
    // This fetch has its own timeout and loop; /latest.json and image loading cannot block it.
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), 3500);
    try {
      const response = await fetch(historyUrl(), {cache: 'no-store', signal: controller.signal});
      if (!response.ok) throw new Error('History unavailable');
      const result = await response.json();
      if (!result || !Array.isArray(result.records)) throw new Error('Invalid history response');
      const records = result.records.slice(0, 200).filter(record => record && typeof record === 'object' && !Array.isArray(record));
      const signature = JSON.stringify(records);
      if (signature !== previous) { render(records); previous = signature; }
      failures = 0;
      status.textContent = 'History connected · ' + records.length + ' recent capture' + (records.length === 1 ? '' : 's') + ' · checked ' + new Date().toLocaleTimeString();
    } catch (_) {
      failures += 1;
      status.textContent = 'History reconnecting… Saved rows remain visible. Attempt ' + failures + '.';
    } finally {
      clearTimeout(timeout);
      setTimeout(pollHistory, 1000);
    }
  }
  pollHistory();
})();
</script>
'''


def augment_dashboard(page: str) -> str:
    """Inject the history table once; preserve existing capture panels and scripts."""
    if _MARKER in page:
        return page
    if "</main>" in page:
        page = page.replace("</main>", _SECTION + "</main>", 1)
    elif "</body>" in page:
        page = page.replace("</body>", _SECTION + "</body>", 1)
    else:
        page += _SECTION
    if "</body>" in page:
        return page.replace("</body>", _SCRIPT + "</body>", 1)
    if "</html>" in page:
        return page.replace("</html>", _SCRIPT + "</html>", 1)
    return page + _SCRIPT
