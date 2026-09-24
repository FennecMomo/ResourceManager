const state = { all: [], selected: null, server: null, github: null, rejectingId: null };
const $ = id => document.getElementById(id);
const categoryText = { Problem: '问题', Suggestion: '建议', Experience: '体验', Other: '其他' };
const statusText = {
  PendingReview: '待接受', Accepted: '已接受', Rejected: '已拒绝', Completed: '已完成',
  UserClosed: '用户已关闭', Open: '处理中'
};
const statusButton = {
  PendingReview: ['待接受', 'action-pending'], Accepted: ['接受', 'action-accepted'],
  Rejected: ['拒绝', 'action-rejected'], Completed: ['完成', 'action-completed']
};

function toast(message) {
  const element = $('toast');
  element.textContent = message;
  element.classList.add('show');
  setTimeout(() => element.classList.remove('show'), 3000);
}

async function api(url, options = {}) {
  const response = await fetch(url, { headers: { 'Content-Type': 'application/json', ...(options.headers || {}) }, ...options });
  if (!response.ok) throw new Error((await response.text()) || `请求失败 ${response.status}`);
  const text = await response.text();
  return text ? JSON.parse(text) : null;
}

async function loadState() {
  const data = await api('/admin/api/state');
  state.server = data.server;
  state.github = data.github;
  $('serverName').textContent = data.server.serverName;
  $('serverVersion').textContent = `服务端 v${data.server.version} · API ${data.server.apiPort}`;
  $('githubStatus').textContent = data.github.account
    ? `已登录 ${data.github.account}${data.github.repository ? ` · ${data.github.owner}/${data.github.repository}` : ''}`
    : '尚未登录 GitHub';
  $('repoOwner').value = data.github.owner || '';
  $('repoName').value = data.github.repository || '';
  $('githubLogin').disabled = !data.github.configured;
  $('githubHint').textContent = data.github.configured ? '' : '尚未配置 GitHub App Client ID。';
}

function normalizedStatus(issue) {
  if (issue.source === 'local') return issue.status;
  if (issue.state === 'open') return 'Open';
  return issue.stateReason === 'not_planned' ? 'Rejected' : 'Completed';
}

function classKey(value) {
  return String(value || 'none').replace(/([a-z])([A-Z])/g, '$1-$2').toLowerCase();
}

function matchesFilters(issue) {
  const source = $('sourceFilter').value;
  const category = $('categoryFilter').value;
  const status = $('statusFilter').value;
  const query = $('search').value.trim().toLowerCase();
  const currentStatus = normalizedStatus(issue);
  const statusMatches = status === 'all'
    || status === currentStatus
    || status === 'active' && ['PendingReview', 'Accepted', 'Open'].includes(currentStatus);
  return (source === 'all' || issue.source === source)
    && (category === 'all' || (category === 'none' ? !issue.category : issue.category === category))
    && statusMatches
    && (!query || issue.title.toLowerCase().includes(query) || (issue.body || '').toLowerCase().includes(query));
}

function filtered() {
  return state.all.filter(matchesFilters).sort((a, b) => new Date(b.updatedUtc) - new Date(a.updatedUtc));
}

async function loadIssues() {
  const selected = state.selected ? { source: state.selected.source, id: state.selected.id } : null;
  const data = await api('/admin/api/issues');
  state.all = [...data.local, ...data.github];
  const fresh = selected ? state.all.find(item => item.source === selected.source && item.id === selected.id) : null;
  selectIssue(fresh && matchesFilters(fresh) ? fresh : null);
}

function badge(text, ...kinds) {
  const element = document.createElement('span');
  element.className = ['badge', ...kinds.map(classKey)].join(' ');
  element.textContent = text;
  return element;
}

function issueBadges(issue) {
  const status = normalizedStatus(issue);
  return [
    badge(issue.source === 'local' ? '本地' : 'GitHub', 'source', issue.source),
    badge(categoryText[issue.category] || '未分类', 'category', issue.category || 'none'),
    badge(statusText[status] || status, 'status', status)
  ];
}

function renderList() {
  const list = $('issueList');
  list.replaceChildren();
  const items = filtered();
  $('empty').style.display = items.length ? 'none' : 'grid';
  for (const issue of items) {
    const item = document.createElement('div');
    const selected = state.selected?.source === issue.source && state.selected?.id === issue.id;
    item.className = `issue category-${classKey(issue.category)} status-${classKey(normalizedStatus(issue))}${selected ? ' selected' : ''}`;
    item.tabIndex = 0;
    const title = document.createElement('h3');
    title.textContent = issue.title;
    const meta = document.createElement('div');
    meta.className = 'meta';
    meta.append(...issueBadges(issue));
    const time = document.createElement('span');
    time.textContent = new Date(issue.updatedUtc).toLocaleString();
    meta.append(time);
    item.append(title, meta);
    item.onclick = () => selectIssue(issue);
    item.onkeydown = event => {
      if (event.key === 'Enter' || event.key === ' ') {
        event.preventDefault();
        selectIssue(issue);
      }
    };
    list.append(item);
  }
}

function fact(list, name, value) {
  const term = document.createElement('dt');
  term.textContent = name;
  const description = document.createElement('dd');
  description.textContent = value || '—';
  list.append(term, description);
}

function selectIssue(issue) {
  state.selected = issue;
  renderList();
  const detail = $('detail');
  detail.replaceChildren();
  if (!issue) {
    const empty = document.createElement('div');
    empty.className = 'empty';
    empty.textContent = '从左侧选择一条 Issue。';
    detail.append(empty);
    return;
  }

  const heading = document.createElement('h2');
  heading.textContent = issue.title;
  const meta = document.createElement('div');
  meta.className = 'meta';
  meta.append(...issueBadges(issue));
  const body = document.createElement('div');
  body.className = 'body';
  body.textContent = issue.body;
  const facts = document.createElement('dl');
  facts.className = 'facts';
  if (issue.source === 'local') {
    fact(facts, '提交者', issue.userName);
    fact(facts, '客户端', `ResourceManager ${issue.appVersion}`);
    fact(facts, '系统', `${issue.osDescription} · ${issue.osArchitecture} / ${issue.processArchitecture}`);
    fact(facts, '提交编号', issue.submissionId);
    if (issue.status === 'Rejected') fact(facts, '拒绝原因', issue.rejectionReason);
  } else {
    fact(facts, '作者', issue.author);
    fact(facts, 'GitHub 编号', `#${issue.number}`);
    fact(facts, '标签', (issue.labels || []).join('、'));
  }
  fact(facts, '创建时间', new Date(issue.createdUtc).toLocaleString());
  fact(facts, '更新时间', new Date(issue.updatedUtc).toLocaleString());
  detail.append(heading, meta, body, facts);

  if (issue.source === 'local' && issue.attachments?.length) {
    const attachmentHeading = document.createElement('h3');
    attachmentHeading.textContent = '附件';
    detail.append(attachmentHeading);
    for (const file of issue.attachments) {
      const row = document.createElement('div');
      row.className = 'attachment';
      const name = document.createElement('span');
      name.textContent = `${file.fileName} · ${formatSize(file.size)}`;
      const link = document.createElement('a');
      link.href = `/admin/api/attachments/${encodeURIComponent(file.id)}`;
      link.textContent = '下载';
      row.append(name, link);
      detail.append(row);
    }
  }

  const actions = document.createElement('div');
  actions.className = 'actions';
  if (issue.source === 'local') {
    for (const [value, [text, className]] of Object.entries(statusButton)) {
      const button = document.createElement('button');
      button.className = className;
      button.textContent = text;
      button.disabled = issue.status === value || issue.status === 'UserClosed';
      button.onclick = () => value === 'Rejected' ? openRejection(issue.id) : setLocal(issue.id, value);
      actions.append(button);
    }
  } else {
    const open = document.createElement('button');
    open.className = 'action-accepted';
    open.textContent = '重新打开';
    open.disabled = issue.state === 'open';
    open.onclick = () => setGitHub(issue.number, 'open', 'reopened');
    const done = document.createElement('button');
    done.className = 'action-completed';
    done.textContent = '标记完成';
    done.onclick = () => setGitHub(issue.number, 'closed', 'completed');
    const reject = document.createElement('button');
    reject.className = 'action-rejected';
    reject.textContent = '不计划处理';
    reject.onclick = () => setGitHub(issue.number, 'closed', 'not_planned');
    const external = document.createElement('button');
    external.className = 'secondary';
    external.textContent = '在 GitHub 打开';
    external.onclick = () => window.open(issue.htmlUrl, '_blank', 'noopener');
    actions.append(open, done, reject, external);
  }
  detail.append(actions);
}

function formatSize(value) {
  return value >= 1048576 ? `${(value / 1048576).toFixed(1)} MB` : `${(value / 1024).toFixed(1)} KB`;
}

function openRejection(id) {
  state.rejectingId = id;
  $('rejectionReason').value = '';
  $('rejectionHint').textContent = '必填，最多 1000 个字符。';
  $('rejectionDialog').showModal();
  $('rejectionReason').focus();
}

async function setLocal(id, status, reason = null) {
  try {
    await api(`/admin/api/issues/${id}/status`, { method: 'PATCH', body: JSON.stringify({ status, reason }) });
    await loadIssues();
    toast(status === 'Rejected' ? '反馈已拒绝并移出处理列表' : '状态已更新');
    return true;
  } catch (error) {
    toast(error.message);
    return false;
  }
}

async function setGitHub(number, stateValue, stateReason) {
  try {
    await api(`/admin/api/github/issues/${number}/state`, { method: 'PATCH', body: JSON.stringify({ state: stateValue, stateReason }) });
    await loadIssues();
    toast('GitHub 状态已更新');
  } catch (error) {
    toast(error.message);
  }
}

async function refresh() {
  try {
    if (state.github?.repository) await api('/admin/api/github/sync', { method: 'POST' });
    await Promise.all([loadState(), loadIssues()]);
    toast('已刷新');
  } catch (error) {
    toast(error.message);
  }
}

function applyFilters() {
  if (state.selected && !matchesFilters(state.selected)) selectIssue(null);
  else renderList();
}

$('sourceFilter').onchange = applyFilters;
$('categoryFilter').onchange = applyFilters;
$('statusFilter').onchange = applyFilters;
$('search').oninput = applyFilters;
$('refresh').onclick = refresh;
$('settingsButton').onclick = () => { $('settingsDialog').showModal(); loadState(); };
$('cancelRejection').onclick = () => $('rejectionDialog').close();
$('confirmRejection').onclick = async () => {
  const reason = $('rejectionReason').value.trim();
  if (!reason) {
    $('rejectionHint').textContent = '请填写拒绝原因。';
    $('rejectionReason').focus();
    return;
  }
  const button = $('confirmRejection');
  button.disabled = true;
  try {
    if (await setLocal(state.rejectingId, 'Rejected', reason)) $('rejectionDialog').close();
    else $('rejectionHint').textContent = '拒绝失败，请检查服务端状态后重试。';
  } finally {
    button.disabled = false;
  }
};

$('githubLogin').onclick = async () => {
  try {
    const flow = await api('/admin/api/github/device/start', { method: 'POST' });
    $('githubHint').textContent = `请在 GitHub 输入代码 ${flow.userCode}`;
    window.open(flow.verificationUri, '_blank', 'noopener');
    const poll = async () => {
      try {
        const result = await api(`/admin/api/github/device/poll/${flow.flowId}`, { method: 'POST' });
        if (result.complete) { toast(`已登录 ${result.account}`); await loadState(); return; }
        setTimeout(poll, Math.max(flow.interval, 5) * 1000);
      } catch (error) { $('githubHint').textContent = error.message; }
    };
    setTimeout(poll, Math.max(flow.interval, 5) * 1000);
  } catch (error) { $('githubHint').textContent = error.message; }
};

$('githubLogout').onclick = async () => {
  try {
    await api('/admin/api/github/logout', { method: 'POST' });
    await Promise.all([loadState(), loadIssues()]);
    toast('已退出 GitHub');
  } catch (error) { toast(error.message); }
};

$('bindRepo').onclick = async () => {
  try {
    await api('/admin/api/github/bind', { method: 'POST', body: JSON.stringify({ owner: $('repoOwner').value, repository: $('repoName').value }) });
    await Promise.all([loadState(), loadIssues()]);
    toast('仓库已绑定');
  } catch (error) { $('githubHint').textContent = error.message; }
};

Promise.all([loadState(), loadIssues()]).catch(error => toast(error.message));
