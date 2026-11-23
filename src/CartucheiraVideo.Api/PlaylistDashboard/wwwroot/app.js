// Cartucheira — Playlist dashboard (Sprint 1, login desde a Sprint 5, menu lateral + gestão de
// Usuários pela própria web desde a Sprint 6). Só JS puro, sem framework/build — ver comentário de
// classe em PlaylistDashboardHost.cs pro porquê (projeto não usa Node/npm em lugar nenhum).

const loginScreen = document.getElementById("login-screen");
const appShell = document.getElementById("app-shell");
const sessionUsername = document.getElementById("session-username");
const sessionRole = document.getElementById("session-role");
const loginForm = document.getElementById("login-form");
const loginError = document.getElementById("login-error");
const logoutButton = document.getElementById("logout-button");
const newProgramForm = document.getElementById("new-program-form");
const notAdminHint = document.getElementById("not-admin-hint");

const mainTitle = document.getElementById("main-title");
const mainSubtitle = document.getElementById("main-subtitle");
const navItems = Array.from(document.querySelectorAll(".nav-item"));
const views = Array.from(document.querySelectorAll(".view"));

// Sprint 6 (2026-11-16) — título/subtítulo do topo por view, pra mudar junto com o menu lateral.
const VIEW_META = {
  "programs-view": { title: "Programas", subtitle: "Organização de materiais por Programa e Data." },
  "teleprompter-view": { title: "Teleprompter", subtitle: "Controle remoto do texto do teleprompter — de qualquer PC na rede local." },
  "gc-view": { title: "GC", subtitle: "Monitoramento e controle remoto do GC (gerador de caracteres) enviado pro vMix." },
  "vinhetas-view": { title: "Vinhetas", subtitle: "Programas e cartuchos de Vinhetas — upload, posição, nome e cor das teclinhas." },
  "trilhas-view": { title: "Trilhas", subtitle: "Programas e cartuchos de Trilhas — upload, posição, nome e cor das teclinhas." },
  "users-view": { title: "Usuários", subtitle: "Quem acessa a dashboard e quais Programas cada um vê/edita." },
};

const programsSection = document.getElementById("programs-section");
const editionsSection = document.getElementById("editions-section");
const espelhoSection = document.getElementById("espelho-section");
const programsList = document.getElementById("programs-list");
const editionsList = document.getElementById("editions-list");
const laudasList = document.getElementById("laudas-list");
const selectedProgramName = document.getElementById("selected-program-name");
const selectedProgramName2 = document.getElementById("selected-program-name-2");
const selectedEditionDate = document.getElementById("selected-edition-date");
const laudaUploadStatus = document.getElementById("lauda-upload-status");
const errorMessage = document.getElementById("error-message");
const newLaudaForm = document.getElementById("new-lauda-form");
const toggleNewLaudaButton = document.getElementById("toggle-new-lauda-button");
const laudaFormTitle = document.getElementById("lauda-form-title");
const laudaFormSubmit = document.getElementById("lauda-form-submit");
const newProgramBackgroundArtSelect = document.getElementById("new-program-background-art");

// Sprint Controle-Remoto-TP (2026-11-17) — tela de Teleprompter (qualquer usuário logado).
const tpStatusBadge = document.getElementById("tp-status-badge");
const tpManualToggle = document.getElementById("tp-manual-toggle");
const tpTextInput = document.getElementById("tp-text-input");
const tpSendTextButton = document.getElementById("tp-send-text-button");
const tpError = document.getElementById("tp-error");

// Parte 3 (2026-11-17) — espelho da tela + controles de rolagem (Play/Pause+velocidade, passo,
// início). Ver comentário da seção "Teleprompter remoto" mais abaixo pro porquê desta abordagem.
const tpMirror = document.getElementById("tp-mirror");
const tpMirrorCueline = document.getElementById("tp-mirror-cueline");
const tpMirrorText = document.getElementById("tp-mirror-text");
const tpJumpStartButton = document.getElementById("tp-jump-start-button");
const tpStepUpButton = document.getElementById("tp-step-up-button");
const tpStepDownButton = document.getElementById("tp-step-down-button");
const tpPlayPauseButton = document.getElementById("tp-play-pause-button");
const tpSpeedSlider = document.getElementById("tp-speed-slider");
const tpSpeedValue = document.getElementById("tp-speed-value");

// Sprint Playlist-TP (2026-11-17) — navegador Programa→Edição→Laudas dentro da própria view
// Teleprompter, pra carregar o texto de uma Lauda já escrita pela produção sem precisar digitar na
// mão. Ver comentário completo no index.html (seção acima de #tp-text-input) sobre as duas decisões
// confirmadas com o usuário (clique só preenche a caixa; lista mostra TODAS as Laudas da Edição).
const tpPlaylistToggleButton = document.getElementById("tp-playlist-toggle-button");
const tpPlaylistPanel = document.getElementById("tp-playlist-panel");
const tpPlaylistProgramSelect = document.getElementById("tp-playlist-program-select");
const tpPlaylistEditionSelect = document.getElementById("tp-playlist-edition-select");
const tpPlaylistLaudasList = document.getElementById("tp-playlist-laudas-list");
const tpPlaylistError = document.getElementById("tp-playlist-error");

// Sprint GC (2026-11-17) — tela de GC (qualquer usuário logado, mesmo critério do Teleprompter).
const gcStatusBadge = document.getElementById("gc-status-badge");
const gcManualToggle = document.getElementById("gc-manual-toggle");
const gcMonitor = document.getElementById("gc-monitor");
const gcMonitorFallback = document.getElementById("gc-monitor-fallback");
const gcSendFieldsButton = document.getElementById("gc-send-fields-button");
const gcClearFieldsButton = document.getElementById("gc-clear-fields-button");
const gcError = document.getElementById("gc-error");

// Sprint GC-Playlist (2026-11-18) — navegador Programa→Edição→Lauda do GC (mesmo princípio do
// tp-playlist-picker) + caixinhas fixas de campo (substituem a antiga #gc-fields-input).
const gcPlaylistToggleButton = document.getElementById("gc-playlist-toggle-button");
const gcPlaylistPanel = document.getElementById("gc-playlist-panel");
const gcPlaylistProgramSelect = document.getElementById("gc-playlist-program-select");
const gcPlaylistEditionSelect = document.getElementById("gc-playlist-edition-select");
const gcPlaylistLaudasList = document.getElementById("gc-playlist-laudas-list");
const gcPlaylistError = document.getElementById("gc-playlist-error");
const gcFieldsDynamicContainer = document.getElementById("gc-fields-dynamic");
const gcFieldsHint = document.getElementById("gc-fields-hint");

// Sprint 6 — tela de Usuários (Admin).
const usersList = document.getElementById("users-list");
const usersError = document.getElementById("users-error");
const addUserButton = document.getElementById("add-user-button");
const userModal = document.getElementById("user-modal");
const userModalTitle = document.getElementById("user-modal-title");
const userForm = document.getElementById("user-form");
const userUsernameInput = document.getElementById("user-username-input");
const userPasswordInput = document.getElementById("user-password-input");
const userPasswordLabel = document.getElementById("user-password-label");
const userIsAdminInput = document.getElementById("user-is-admin-input");
const userProgramsRow = document.getElementById("user-programs-row");
const userProgramsChecklist = document.getElementById("user-programs-checklist");
const userFormError = document.getElementById("user-form-error");
const userFormCancel = document.getElementById("user-form-cancel");
const userRemoveButton = document.getElementById("user-remove-button");

// Sprint GC-Campos-Fixos (2026-11-17) — modal "GC padrão do Programa" (ver comentário completo
// no index.html) e os elementos novos do formulário de Lauda (caixinhas dinâmicas no lugar da
// antiga textarea livre #new-lauda-gc).
// Sprint GC-Campos-Globais (2026-11-19, revisão): #program-gc-input (textarea) trocado por
// #program-gc-fields-dynamic + #program-gc-hint — mesmo padrão de newLaudaGcFieldsContainer.
const programGcEditButton = document.getElementById("program-gc-edit-button");
const programGcModal = document.getElementById("program-gc-modal");
const programGcForm = document.getElementById("program-gc-form");
const programGcFieldsDynamicContainer = document.getElementById("program-gc-fields-dynamic");
const programGcHint = document.getElementById("program-gc-hint");
const programGcFormCancel = document.getElementById("program-gc-form-cancel");
const programGcFormError = document.getElementById("program-gc-form-error");
const newLaudaGcFieldsContainer = document.getElementById("new-lauda-gc-fields");
const newLaudaGcHint = document.getElementById("new-lauda-gc-hint");

let selectedProgramId = null;
let selectedProgramName_ = null;
let selectedEditionId = null;

// Sprint 5 (2026-11-16) — usuário autenticado da sessão atual (ver /api/auth/me), null enquanto
// deslogado. Só guarda o que a UI precisa decidir (isAdmin pra mostrar/esconder "Criar Programa"/
// menu "Usuários").
let currentUser = null;

// Sprint 3 (2026-11-16): cache da última lista de Laudas carregada — usado pra calcular a nova
// ordem ao arrastar-e-soltar (reorderLaudas) e pra achar os dados completos de uma Lauda ao
// clicar nela pra editar (openLaudaForEdit), sem precisar de outra ida ao servidor pra cada clique.
let currentLaudas = [];

// Id da Lauda sendo editada no formulário — null quando o formulário está em modo "Nova Lauda".
let editingLaudaId = null;

// Id da Lauda sendo arrastada no momento (drag-and-drop nativo HTML5) — ver renderLaudas.
let draggedLaudaId = null;

// Sprint 6 (2026-11-16) — cache dos Programas (pra montar o checklist do modal de usuário e pra
// mostrar o NOME dos Programas liberados na lista de Usuários, não só o Id cru) e do usuário sendo
// editado no modal (null = modal em modo "Novo usuário").
let currentPrograms = [];
let editingUserId = null;

// Sprint GC-Campos-Globais (2026-11-19, pedido do usuário "isso não teria que vir só setup que tá
// na cartucheira na config do gc"): só existe UM Título de vMix fixo pro sistema inteiro (nunca um
// por Programa, ver comentário de classe do Coordinator) — então os NOMES dos campos de GC também
// são globais, e vêm sempre de Configurações → GC (mesma lista do "Campos do GC (mockup)" que já
// alimenta o monitoramento, GET /api/gc/layout). gcFieldNamesCache evita rebuscar a cada abertura de
// formulário na mesma sessão de página — se o Admin mudar Configurações → GC no meio da sessão,
// precisa recarregar a página pra pegar os nomes novos aqui (mesmo critério de cache já usado em
// tpPlaylistProgramsCache/currentPrograms nesta dashboard).
let gcFieldNamesCache = null;

async function ensureGcFieldNames() {
  if (gcFieldNamesCache !== null) {
    return gcFieldNamesCache;
  }
  try {
    const layout = await apiGet("/api/gc/layout");
    gcFieldNamesCache = layout.map((field) => field.name);
  } catch (err) {
    gcFieldNamesCache = []; // falha honesta — formulários caem pro aviso "nenhum campo cadastrado".
  }
  return gcFieldNamesCache;
}

// Sprint Playlist-TP (2026-11-17) — estado do navegador Programa→Edição→Laudas DENTRO da view
// Teleprompter, deliberadamente SEPARADO de selectedProgramId/selectedEditionId/currentLaudas (que
// pertencem à view "Programas" — navegar aqui nunca deve mexer no que está aberto lá, e vice-versa).
// tpPlaylistProgramsCache evita rebuscar a lista de Programas toda vez que o painel é reaberto na
// mesma sessão (não muda com frequência; se mudar, reabrir a página pega a lista nova).
let tpPlaylistProgramsCache = null;
let tpPlaylistProgramId = null;
let tpPlaylistEditionId = null;

// Sprint Controle-Remoto-TP, parte 2 (2026-11-17, pedido do usuário "2 segundos e muito,
// precisamos pensar outra alternativa" ao polling de 2s da parte 1): tpEventSource é a conexão de
// Server-Sent Events (só existe enquanto a view "teleprompter-view" estiver aberta, ver
// showView/navItems abaixo) — o servidor empurra o estado na hora que algo muda, em vez desta
// página ficar perguntando de novo e de novo. tpManualState guarda o último "manualOverride"
// conhecido, pra decidir se o checkbox/textarea devem ficar habilitados sem esperar a resposta de
// uma ação do próprio usuário.
let tpEventSource = null;
let tpManualState = false;

// Sprint GC (2026-11-17) — mesmo papel de tpEventSource/tpManualState, pra view "gc-view".
// gcFieldLayout guarda a lista de posições do mockup (Configurações → GC), buscada uma vez ao
// abrir a view — não muda em tempo real, não precisa de SSE próprio (diferente dos valores dos
// campos, que mudam via applyGcState).
let gcEventSource = null;
let gcManualState = false;
let gcFieldLayout = [];

// Sprint GC-Playlist (2026-11-18) — mesmo princípio de isolamento de tpPlaylistProgramId/
// tpPlaylistEditionId acima, só que pro navegador Programa→Edição→Lauda dentro da view "gc-view".
// Sprint GC-Campos-Globais (2026-11-19): o ESQUEMA de campos (quais caixinhas aparecem) não depende
// mais de qual Programa é escolhido aqui — vem sempre de gcFieldNamesCache (global). Este picker
// serve só pra achar e carregar os VALORES de uma Lauda já escrita, igual o do Teleprompter.
let gcPlaylistProgramsCache = null;
let gcPlaylistProgramId = null;
let gcPlaylistEditionId = null;

// Parte 3 (2026-11-17) — tpSpeedDragging evita a mesma "briga" que já existe pro #tp-text-input
// (ver applyTeleprompterState): enquanto o usuário está arrastando o slider de velocidade, um
// evento "state" chegando no meio do arrasto não deveria resetar a posição dele.
let tpSpeedDragging = false;

function showError(message) {
  errorMessage.textContent = message;
  errorMessage.classList.remove("hidden");
}

function clearError() {
  errorMessage.classList.add("hidden");
  errorMessage.textContent = "";
}

// Sprint 5 (2026-11-16): sessão expirada (ou nunca logada) — qualquer chamada de API devolvendo
// 401 cai aqui, não só o carregamento inicial. Mostra a tela de login de novo em vez de deixar a
// dashboard num estado quebrado tentando usar dados que não vêm mais.
function handleUnauthorized() {
  showLoginSection();
  throw new Error("sessão expirada — faça login de novo.");
}

async function apiGet(path) {
  const response = await fetch(path);
  if (response.status === 401) {
    return handleUnauthorized();
  }
  if (!response.ok) {
    throw new Error(`Falha ao carregar (${response.status}).`);
  }
  return response.json();
}

async function apiPost(path, body) {
  const response = await fetch(path, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
  if (response.status === 401) {
    return handleUnauthorized();
  }
  if (!response.ok) {
    const text = await response.text();
    throw new Error(text || `Falha ao salvar (${response.status}).`);
  }
  return response.json();
}

async function apiPut(path, body) {
  const response = await fetch(path, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
  if (response.status === 401) {
    return handleUnauthorized();
  }
  if (!response.ok) {
    const text = await response.text();
    throw new Error(text || `Falha ao salvar (${response.status}).`);
  }
  return response.json();
}

/// Sprint 6 (2026-11-16) — igual a apiPost/apiPut, só que pra DELETE (sem corpo, usado em
/// HandleDeleteUser do lado do servidor, que lê o Id da query string em vez do corpo).
async function apiDelete(path) {
  const response = await fetch(path, { method: "DELETE" });
  if (response.status === 401) {
    return handleUnauthorized();
  }
  if (!response.ok) {
    const text = await response.text();
    throw new Error(text || `Falha ao remover (${response.status}).`);
  }
  return response.json();
}

/// Sprint 3 (2026-11-16) — presets de Arte de Fundo já cadastrados em Configurações → Arte de
/// Fundo, pro <select> do formulário "Novo Programa". Se a rota falhar (ex.: Cartucheira ainda não
/// atualizada pra esta sprint), não trava o resto da dashboard — só fica sem opções nesse campo.
async function loadBackgroundArtPresets() {
  try {
    const presets = await apiGet("/api/playlist/background-art-presets");
    newProgramBackgroundArtSelect.innerHTML = "";
    const noneOption = document.createElement("option");
    noneOption.value = "";
    noneOption.textContent = "Sem fundo padrão";
    newProgramBackgroundArtSelect.appendChild(noneOption);
    presets.forEach((preset) => {
      const option = document.createElement("option");
      option.value = preset.id;
      option.textContent = preset.name;
      newProgramBackgroundArtSelect.appendChild(option);
    });
  } catch (err) {
    // Silencioso de propósito — ver comentário acima.
  }
}

async function loadPrograms() {
  clearError();
  try {
    const programs = await apiGet("/api/playlist/programs");
    currentPrograms = programs;
    programsList.innerHTML = "";
    programs.forEach((program) => {
      const item = document.createElement("li");
      item.textContent = program.name;
      item.className = "clickable";
      item.addEventListener("click", () => openProgram(program));
      programsList.appendChild(item);
    });
  } catch (err) {
    showError(err.message);
  }
}

async function openProgram(program) {
  selectedProgramId = program.id;
  selectedProgramName_ = program.name;
  selectedProgramName.textContent = program.name;
  programsSection.classList.add("hidden");
  editionsSection.classList.remove("hidden");
  await loadEditions();
}

async function loadEditions() {
  clearError();
  try {
    const editions = await apiGet(`/api/playlist/editions?programId=${encodeURIComponent(selectedProgramId)}`);
    editionsList.innerHTML = "";
    editions.forEach((edition) => {
      const item = document.createElement("li");
      item.textContent = edition.dateIso;
      item.className = "clickable";
      item.addEventListener("click", () => openEdition(edition));
      editionsList.appendChild(item);
    });
  } catch (err) {
    showError(err.message);
  }
}

async function openEdition(edition) {
  selectedEditionId = edition.id;
  selectedProgramName2.textContent = selectedProgramName_;
  selectedEditionDate.textContent = edition.dateIso;
  editionsSection.classList.add("hidden");
  espelhoSection.classList.remove("hidden");
  closeLaudaForm();
  await loadLaudas();
}

function laudaQuery() {
  return `programId=${encodeURIComponent(selectedProgramId)}&editionId=${encodeURIComponent(selectedEditionId)}`;
}

function describeLauda(lauda) {
  const parts = [];
  if (lauda.fileName) parts.push("VT");
  if (lauda.gcFields && Object.keys(lauda.gcFields).length > 0) parts.push("GC");
  if (lauda.tpText) parts.push("TP");
  if (lauda.offText) parts.push("OFF");
  if (lauda.images && lauda.images.length > 0) parts.push(`Imagens (${lauda.images.length})`);
  if (lauda.notes) parts.push("Obs.");
  return parts.length > 0 ? parts.join(" · ") : "sem elementos ainda";
}

/// Sprint GC-Campos-Globais (2026-11-19, revisão do v1.0.104): monta uma caixinha de texto PRA CADA
/// nome cadastrado GLOBALMENTE em Configurações → GC (gcFieldNamesCache) — pré-preenchida com
/// `values[nome]` quando existir — chamada por openLaudaCreateForm (values vazio) e
/// openLaudaForEdit (values = lauda.gcFields). Quem chama precisa garantir gcFieldNamesCache já
/// carregado (await ensureGcFieldNames()) antes — esta função em si é síncrona, só lê o cache.
function renderLaudaGcFieldInputs(values) {
  const fieldNames = gcFieldNamesCache || [];
  newLaudaGcFieldsContainer.innerHTML = "";
  newLaudaGcHint.classList.toggle("hidden", fieldNames.length > 0);
  fieldNames.forEach((name) => {
    const row = document.createElement("div");
    row.className = "gc-field-row";
    const label = document.createElement("label");
    label.textContent = name;
    const input = document.createElement("input");
    input.type = "text";
    input.dataset.gcFieldName = name;
    input.value = (values && values[name]) || "";
    row.appendChild(label);
    row.appendChild(input);
    newLaudaGcFieldsContainer.appendChild(row);
  });
}

/// Inverso de renderLaudaGcFieldInputs — lê as caixinhas de volta pro dicionário nome→valor que a
/// API espera. Manda todos os nomes do Programa, mesmo os deixados em branco (um campo vazio é uma
/// escolha válida do produtor, ex.: um GC só de "Nome" sem "Cargo" — não precisa virar ausência de
/// chave; o servidor (FilterGcFieldsToProgramSchema) é quem decide o que realmente é gravado).
function collectLaudaGcFieldsFromForm() {
  const fields = {};
  newLaudaGcFieldsContainer.querySelectorAll("[data-gc-field-name]").forEach((input) => {
    fields[input.dataset.gcFieldName] = input.value;
  });
  return fields;
}

async function loadLaudas() {
  clearError();
  try {
    currentLaudas = await apiGet(`/api/playlist/laudas?${laudaQuery()}`);
    renderLaudas();
  } catch (err) {
    showError(err.message);
  }
}

/// Sprint 3 (2026-11-16): cada <li> agora é clicável (abre a Lauda pra editar) E arrastável
/// (reordenar, via drag-and-drop nativo HTML5 — sem biblioteca nenhuma, mesmo princípio de "JS puro"
/// do resto da dashboard). Soltar sobre outra Lauda insere ANTES dela na nova ordem.
function renderLaudas() {
  laudasList.innerHTML = "";
  currentLaudas.forEach((lauda) => {
    const item = document.createElement("li");
    item.draggable = true;
    item.dataset.laudaId = lauda.id;

    const handle = document.createElement("span");
    handle.className = "drag-handle";
    handle.textContent = "⠿";
    handle.title = "Arraste pra reordenar";

    const body = document.createElement("div");
    body.className = "lauda-body";
    const title = document.createElement("div");
    title.className = "lauda-title";
    title.textContent = `${lauda.order}. ${lauda.title}${lauda.type ? " — " + lauda.type : ""}`;
    const details = document.createElement("div");
    details.className = "lauda-details";
    const durationText = lauda.durationPlanned ? `Duração prevista: ${lauda.durationPlanned} · ` : "";
    details.textContent = `${durationText}${describeLauda(lauda)}`;
    body.appendChild(title);
    body.appendChild(details);

    item.appendChild(handle);
    item.appendChild(body);

    item.addEventListener("click", () => openLaudaForEdit(lauda));

    item.addEventListener("dragstart", (event) => {
      draggedLaudaId = lauda.id;
      item.classList.add("dragging");
      event.dataTransfer.effectAllowed = "move";
    });
    item.addEventListener("dragend", () => {
      item.classList.remove("dragging");
      draggedLaudaId = null;
    });
    item.addEventListener("dragover", (event) => {
      event.preventDefault();
      item.classList.add("drag-over");
    });
    item.addEventListener("dragleave", () => {
      item.classList.remove("drag-over");
    });
    item.addEventListener("drop", async (event) => {
      event.preventDefault();
      item.classList.remove("drag-over");
      if (!draggedLaudaId || draggedLaudaId === lauda.id) return;
      await reorderLaudas(draggedLaudaId, lauda.id);
    });

    laudasList.appendChild(item);
  });
}

/// Sprint 3 (2026-11-16) — calcula a nova sequência de Ids (move draggedId pra antes de targetId,
/// com base na ordem atual em currentLaudas) e manda pro servidor; em caso de erro, recarrega do
/// servidor em vez de deixar a lista na tela num estado que não bate com o disco.
async function reorderLaudas(draggedId, targetId) {
  clearError();
  const ids = currentLaudas.map((l) => l.id);
  const fromIndex = ids.indexOf(draggedId);
  const toIndex = ids.indexOf(targetId);
  if (fromIndex === -1 || toIndex === -1) return;

  ids.splice(fromIndex, 1);
  ids.splice(ids.indexOf(targetId), 0, draggedId);

  try {
    currentLaudas = await apiPut("/api/playlist/laudas/reorder", {
      programId: selectedProgramId,
      editionId: selectedEditionId,
      orderedIds: ids,
    });
    renderLaudas();
  } catch (err) {
    showError(err.message);
    await loadLaudas();
  }
}

function resetLaudaForm() {
  newLaudaForm.reset();
}

async function openLaudaCreateForm() {
  editingLaudaId = null;
  resetLaudaForm();
  await ensureGcFieldNames();
  renderLaudaGcFieldInputs({});
  laudaFormTitle.textContent = "Nova Lauda";
  laudaFormSubmit.textContent = "Criar Lauda";
  newLaudaForm.classList.remove("hidden");
  toggleNewLaudaButton.textContent = "− Fechar";
}

/// Reaproveita o mesmo formulário "Nova Lauda", só pré-preenchido — decisão do usuário (Sprint 3,
/// 2026-11-16) em vez de um formulário de edição separado. VT/Imagens não são pré-carregados nos
/// campos de arquivo (navegador não permite preencher <input type="file"> por JS) — se o produtor
/// escolher um arquivo novo aqui, ele substitui/adiciona normalmente ao salvar (mesmo upload de
/// sempre); se não escolher nada, o VT/imagens já existentes da Lauda continuam intocados.
async function openLaudaForEdit(lauda) {
  editingLaudaId = lauda.id;
  resetLaudaForm();
  document.getElementById("new-lauda-title").value = lauda.title === "(sem título)" ? "" : lauda.title;
  document.getElementById("new-lauda-type").value = lauda.type || "";
  document.getElementById("new-lauda-duration").value = lauda.durationPlanned || "";
  await ensureGcFieldNames();
  renderLaudaGcFieldInputs(lauda.gcFields || {});
  document.getElementById("new-lauda-tp").value = lauda.tpText || "";
  document.getElementById("new-lauda-off").value = lauda.offText || "";
  document.getElementById("new-lauda-notes").value = lauda.notes || "";
  laudaFormTitle.textContent = `Editar Lauda — ${lauda.title}`;
  laudaFormSubmit.textContent = "Salvar alterações";
  newLaudaForm.classList.remove("hidden");
  toggleNewLaudaButton.textContent = "− Fechar";
  newLaudaForm.scrollIntoView({ behavior: "smooth", block: "start" });
}

function closeLaudaForm() {
  editingLaudaId = null;
  resetLaudaForm();
  newLaudaForm.classList.add("hidden");
  toggleNewLaudaButton.textContent = "+ Nova Lauda";
}

async function uploadFile(path, file) {
  const response = await fetch(path, { method: "PUT", body: file });
  if (response.status === 401) {
    return handleUnauthorized();
  }
  if (!response.ok) {
    const text = await response.text();
    throw new Error(text || `Falha ao enviar arquivo (${response.status}).`);
  }
  return response.json();
}

// Sprint Cartucheira-Dashboard (2026-11-23, pedido do usuário "o menu vinhetas, trilhas, parece
// estar meio incompleto. Vamos colocar eles no dashboard? Tem dois módulos lá, vinhetas e trilhas
// [...] a gente cria os programas e cada programa a gente coloca lá uma pasta raiz de áudios e
// cada programa mesmo formato de upload de vídeo [...] aí lá do dashboard eu posso organizar o
// posicionamento das teclinhas [...] e renomear, colocar cor"): os dois módulos (Vinhetas/Trilhas)
// são estruturalmente idênticos — só muda o prefixo dos ids no HTML e o segmento "{bank}" da rota
// (/api/cartucheira/{bank}/...) — por isso uma função fábrica só, chamada uma vez por banco, em vez
// de duplicar toda essa lógica duas vezes. Mesmo princípio de reorder por drag-and-drop nativo
// HTML5 de renderLaudas/reorderLaudas (ver lá), reaproveitando até as mesmas classes CSS
// (.laudas-list/.lauda-title/.lauda-body/.drag-handle/.dragging/.drag-over) — a estrutura visual de
// uma linha é a mesma (alça + título + detalhe), só o conteúdo que muda.
const CART_DEFAULT_COLOR = { vinhetas: "#2D7DFF", trilhas: "#2DBE60" };

/// Cor pode vir nula (nenhuma escolhida ainda) ou em formato ARGB8 ("#FF2D7DFF", como o app desktop
/// grava via ColorDialog) — <input type="color"> só aceita "#RRGGBB" (6 dígitos), então aqui tira o
/// par de dígitos de alfa quando existir. Nunca manda ARGB de volta pro servidor (ver
/// cartSetCartridgeColor abaixo) — o valor que volta do próprio <input type="color"> já é RGB puro,
/// e o conversor do lado do app desktop (HexColorToBrushConverter) aceita os dois formatos.
function cartNormalizeColorForInput(bank, hex) {
  if (!hex) return CART_DEFAULT_COLOR[bank];
  const clean = hex.replace("#", "");
  if (clean.length === 8) return `#${clean.slice(2)}`;
  if (clean.length === 6) return `#${clean}`;
  return CART_DEFAULT_COLOR[bank];
}

function createCartucheiraModule(bank) {
  const programsSection = document.getElementById(`${bank}-programs-section`);
  const cartridgesSection = document.getElementById(`${bank}-cartridges-section`);
  const programsList = document.getElementById(`${bank}-programs-list`);
  const newProgramForm = document.getElementById(`${bank}-new-program-form`);
  const newProgramNameInput = document.getElementById(`${bank}-new-program-name`);
  const notAdminHint = document.getElementById(`${bank}-not-admin-hint`);
  const noRootHint = document.getElementById(`${bank}-no-root-hint`);
  const backButton = document.getElementById(`${bank}-back-button`);
  const selectedProgramNameEl = document.getElementById(`${bank}-selected-program-name`);
  const uploadInput = document.getElementById(`${bank}-upload-input`);
  const uploadStatus = document.getElementById(`${bank}-upload-status`);
  const cartridgesList = document.getElementById(`${bank}-cartridges-list`);
  const cartridgesError = document.getElementById(`${bank}-cartridges-error`);

  let selectedProgramId = null;
  let currentCartridges = [];
  let draggedCartridgeId = null;

  function showModuleError(message) {
    cartridgesError.textContent = message;
    cartridgesError.classList.remove("hidden");
  }

  function clearModuleError() {
    cartridgesError.classList.add("hidden");
    cartridgesError.textContent = "";
  }

  async function loadPrograms() {
    clearModuleError();
    try {
      const programs = await apiGet(`/api/cartucheira/${bank}/programs`);
      programsList.innerHTML = "";
      noRootHint.classList.toggle("hidden", programs.length > 0);
      programs.forEach((program) => {
        const item = document.createElement("li");
        item.textContent = program.name;
        item.className = "clickable";
        item.addEventListener("click", () => openProgram(program));
        programsList.appendChild(item);
      });
    } catch (err) {
      showModuleError(err.message);
    }
  }

  async function openProgram(program) {
    selectedProgramId = program.id;
    selectedProgramNameEl.textContent = program.name;
    programsSection.classList.add("hidden");
    cartridgesSection.classList.remove("hidden");
    await loadCartridges();
  }

  function backToPrograms() {
    selectedProgramId = null;
    cartridgesSection.classList.add("hidden");
    programsSection.classList.remove("hidden");
  }

  async function loadCartridges() {
    clearModuleError();
    try {
      currentCartridges = await apiGet(`/api/cartucheira/${bank}/cartridges?programId=${encodeURIComponent(selectedProgramId)}`);
      renderCartridges();
    } catch (err) {
      showModuleError(err.message);
    }
  }

  /// Mesmo princípio de drag-and-drop nativo HTML5 de renderLaudas (ver lá pro comentário completo)
  /// — soltar sobre outro cartucho insere ANTES dele na nova ordem.
  function renderCartridges() {
    cartridgesList.innerHTML = "";
    currentCartridges.forEach((cartridge) => {
      const item = document.createElement("li");
      item.draggable = true;
      item.dataset.cartridgeId = cartridge.id;

      const handle = document.createElement("span");
      handle.className = "drag-handle";
      handle.textContent = "⠿";
      handle.title = "Arraste pra reordenar";

      const body = document.createElement("div");
      body.className = "lauda-body";
      const title = document.createElement("div");
      title.className = "lauda-title clickable";
      title.textContent = `${cartridge.order}. ${cartridge.title}`;
      title.title = "Clique pra renomear";
      title.addEventListener("click", (event) => {
        event.stopPropagation();
        renameCartridge(cartridge);
      });
      const details = document.createElement("div");
      details.className = "lauda-details";
      details.textContent = cartridge.fileName;
      body.appendChild(title);
      body.appendChild(details);

      const colorInput = document.createElement("input");
      colorInput.type = "color";
      colorInput.className = "cart-color-input";
      colorInput.value = cartNormalizeColorForInput(bank, cartridge.color);
      colorInput.title = "Cor do cartucho";
      colorInput.addEventListener("click", (event) => event.stopPropagation());
      colorInput.addEventListener("change", () => setCartridgeColor(cartridge, colorInput.value));

      const deleteButton = document.createElement("button");
      deleteButton.type = "button";
      deleteButton.className = "danger";
      deleteButton.textContent = "Apagar";
      deleteButton.addEventListener("click", async (event) => {
        event.stopPropagation();
        if (!window.confirm(`Apagar o cartucho "${cartridge.title}"? Esta ação não pode ser desfeita.`)) {
          return;
        }
        await deleteCartridge(cartridge);
      });

      const actions = document.createElement("div");
      actions.className = "cart-row-actions";
      actions.appendChild(colorInput);
      actions.appendChild(deleteButton);

      item.appendChild(handle);
      item.appendChild(body);
      item.appendChild(actions);

      item.addEventListener("dragstart", (event) => {
        draggedCartridgeId = cartridge.id;
        item.classList.add("dragging");
        event.dataTransfer.effectAllowed = "move";
      });
      item.addEventListener("dragend", () => {
        item.classList.remove("dragging");
        draggedCartridgeId = null;
      });
      item.addEventListener("dragover", (event) => {
        event.preventDefault();
        item.classList.add("drag-over");
      });
      item.addEventListener("dragleave", () => item.classList.remove("drag-over"));
      item.addEventListener("drop", async (event) => {
        event.preventDefault();
        item.classList.remove("drag-over");
        if (!draggedCartridgeId || draggedCartridgeId === cartridge.id) {
          return;
        }
        await reorderCartridges(draggedCartridgeId, cartridge.id);
      });

      cartridgesList.appendChild(item);
    });
  }

  async function reorderCartridges(draggedId, targetId) {
    clearModuleError();
    const ids = currentCartridges.map((c) => c.id);
    const fromIndex = ids.indexOf(draggedId);
    const toIndex = ids.indexOf(targetId);
    if (fromIndex === -1 || toIndex === -1) {
      return;
    }

    ids.splice(fromIndex, 1);
    ids.splice(ids.indexOf(targetId), 0, draggedId);

    try {
      currentCartridges = await apiPut(`/api/cartucheira/${bank}/cartridges/reorder`, { programId: selectedProgramId, orderedIds: ids });
      renderCartridges();
    } catch (err) {
      showModuleError(err.message);
      await loadCartridges();
    }
  }

  async function renameCartridge(cartridge) {
    const newTitle = window.prompt("Novo nome do cartucho:", cartridge.title);
    if (newTitle === null || newTitle.trim() === "" || newTitle === cartridge.title) {
      return;
    }

    try {
      await apiPost(`/api/cartucheira/${bank}/cartridges/rename`, { programId: selectedProgramId, cartridgeId: cartridge.id, title: newTitle });
      await loadCartridges();
    } catch (err) {
      showModuleError(err.message);
    }
  }

  async function setCartridgeColor(cartridge, colorHex) {
    try {
      await apiPost(`/api/cartucheira/${bank}/cartridges/color`, { programId: selectedProgramId, cartridgeId: cartridge.id, color: colorHex });
      await loadCartridges();
    } catch (err) {
      showModuleError(err.message);
    }
  }

  async function deleteCartridge(cartridge) {
    try {
      await apiDelete(`/api/cartucheira/${bank}/cartridges?programId=${encodeURIComponent(selectedProgramId)}&cartridgeId=${encodeURIComponent(cartridge.id)}`);
      await loadCartridges();
    } catch (err) {
      showModuleError(err.message);
    }
  }

  async function uploadFiles(fileList) {
    const files = Array.from(fileList || []);
    if (files.length === 0) {
      return;
    }

    uploadStatus.classList.remove("hidden");
    for (const file of files) {
      uploadStatus.textContent = `Enviando "${file.name}"...`;
      try {
        await uploadFile(`/api/cartucheira/${bank}/cartridges/upload?programId=${encodeURIComponent(selectedProgramId)}&filename=${encodeURIComponent(file.name)}`, file);
      } catch (err) {
        showModuleError(`Falha ao enviar "${file.name}": ${err.message}`);
      }
    }

    uploadStatus.textContent = "";
    uploadStatus.classList.add("hidden");
    uploadInput.value = "";
    await loadCartridges();
  }

  newProgramForm.addEventListener("submit", async (event) => {
    event.preventDefault();
    clearModuleError();
    try {
      await apiPost(`/api/cartucheira/${bank}/programs`, { name: newProgramNameInput.value });
      newProgramNameInput.value = "";
      await loadPrograms();
    } catch (err) {
      showModuleError(err.message);
    }
  });

  backButton.addEventListener("click", backToPrograms);
  uploadInput.addEventListener("change", () => uploadFiles(uploadInput.files));

  return {
    setAdminUi(isAdmin) {
      newProgramForm.classList.toggle("hidden", !isAdmin);
      notAdminHint.classList.toggle("hidden", isAdmin);
    },
    async activate() {
      clearModuleError();
      backToPrograms();
      await loadPrograms();
    },
  };
}

const vinhetasModule = createCartucheiraModule("vinhetas");
const trilhasModule = createCartucheiraModule("trilhas");

// Sprint 5 (2026-11-16) — login/sessão. showLoginSection/showAppContent controlam qual tela
// aparece; checkAuthAndInit roda uma vez ao carregar a página E de novo depois de um login
// bem-sucedido (ver login-form abaixo).

function showLoginSection() {
  currentUser = null;
  appShell.classList.add("hidden");
  loginScreen.classList.remove("hidden");
}

/// Sprint 6 (2026-11-16) — troca de "view" dentro da casca (Programas/Usuários), espelhando o item
/// clicado do menu lateral no título do topo. Não recarrega dados sozinho — quem chama decide o que
/// carregar (ver os listeners de nav-item e checkAuthAndInit).
function showView(viewId) {
  views.forEach((view) => view.classList.toggle("hidden", view.id !== viewId));
  navItems.forEach((item) => item.classList.toggle("active", item.dataset.view === viewId));
  const meta = VIEW_META[viewId];
  if (meta) {
    mainTitle.textContent = meta.title;
    mainSubtitle.textContent = meta.subtitle;
  }
}

function showAppContent() {
  loginScreen.classList.add("hidden");
  appShell.classList.remove("hidden");
  sessionUsername.textContent = currentUser.username;
  sessionRole.textContent = currentUser.isAdmin ? "Administrador" : "Produtor";

  // Pedido confirmado com o usuário (Sprint 5): só Admin cria Programa novo — usuário comum só
  // cria Data/Lauda DENTRO de Programas que já tem acesso (ver PlaylistDashboardCoordinator.
  // HandleCreateProgram, que rejeita com 403 mesmo que alguém force a chamada sem passar por aqui).
  newProgramForm.classList.toggle("hidden", !currentUser.isAdmin);
  notAdminHint.classList.toggle("hidden", currentUser.isAdmin);

  // Sprint GC-Campos-Fixos (2026-11-17) — só Admin edita os campos do GC padrão de um Programa já
  // criado, mesmo critério de "Criar Programa" acima.
  programGcEditButton.classList.toggle("hidden", !currentUser.isAdmin);

  // Sprint Cartucheira-Dashboard (2026-11-23) — mesmo critério "só Admin cria Programa novo" das
  // Vinhetas/Trilhas (upload/renomear/cor/reordenar/apagar cartucho continuam liberados pra
  // qualquer usuário logado, sem checklist de Programas liberados — ver comentário de classe do
  // Coordinator).
  vinhetasModule.setAdminUi(currentUser.isAdmin);
  trilhasModule.setAdminUi(currentUser.isAdmin);

  // Sprint 6 (2026-11-16) — menu "Usuários" só existe pra Admin (decisão confirmada: só Admin
  // gerencia usuários/permissões).
  navItems.forEach((item) => {
    if (item.dataset.view === "users-view") {
      item.classList.toggle("hidden", !currentUser.isAdmin);
    }
  });

  showView("programs-view");
}

async function checkAuthAndInit() {
  try {
    currentUser = await apiGet("/api/auth/me");
  } catch (err) {
    return; // handleUnauthorized() já mostrou a tela de login.
  }

  showAppContent();
  clearError();
  await loadPrograms();
  await loadBackgroundArtPresets();
}

navItems.forEach((item) => {
  item.addEventListener("click", async () => {
    const viewId = item.dataset.view;

    // Sprint Controle-Remoto-TP (2026-11-17, EventSource desde a parte 2): a conexão de estado do
    // Teleprompter só fica aberta enquanto a view dele estiver aberta — sair da view (clicar em
    // qualquer outro item do menu) fecha, mesmo que seja pra outra view que não "desliga" nada
    // sozinha. Sprint GC (2026-11-17): mesmo princípio pro stream do GC.
    stopTeleprompterStream();
    stopGcStream();

    showView(viewId);

    if (viewId === "users-view") {
      await loadUsers();
    } else if (viewId === "teleprompter-view") {
      startTeleprompterStream();
    } else if (viewId === "gc-view") {
      startGcStream();
    } else if (viewId === "vinhetas-view") {
      await vinhetasModule.activate();
    } else if (viewId === "trilhas-view") {
      await trilhasModule.activate();
    }
  });
});

loginForm.addEventListener("submit", async (event) => {
  event.preventDefault();
  loginError.classList.add("hidden");
  const usernameInput = document.getElementById("login-username");
  const passwordInput = document.getElementById("login-password");

  try {
    await apiPost("/api/auth/login", { username: usernameInput.value, password: passwordInput.value });
    passwordInput.value = "";
    await checkAuthAndInit();
  } catch (err) {
    loginError.textContent = err.message;
    loginError.classList.remove("hidden");
  }
});

logoutButton.addEventListener("click", async () => {
  stopTeleprompterStream();
  stopGcStream();
  // Sprint Playlist-TP (2026-11-17): limpa o cache/estado do navegador Programa→Edição→Laudas do
  // Teleprompter — sem isso, um login seguinte (de outro usuário, na mesma aba) herdaria a lista de
  // Programas e a seleção de quem saiu.
  tpPlaylistProgramsCache = null;
  resetTeleprompterPlaylistPicker();
  tpPlaylistPanel.classList.add("hidden");
  tpPlaylistToggleButton.textContent = "▸ Carregar texto de uma Playlist";
  // Sprint GC-Playlist (2026-11-18): mesma limpeza, agora pro navegador Programa→Edição→Laudas do GC.
  gcPlaylistProgramsCache = null;
  resetGcPlaylistPicker();
  gcPlaylistPanel.classList.add("hidden");
  gcPlaylistToggleButton.textContent = "▸ Carregar de uma Playlist";
  try {
    await fetch("/api/auth/logout", { method: "POST" });
  } catch (err) {
    // Sem problema — mostra a tela de login mesmo assim, o pior caso é a sessão expirar sozinha.
  }
  selectedProgramId = null;
  selectedEditionId = null;
  showLoginSection();
});

document.getElementById("new-program-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  clearError();
  const nameInput = document.getElementById("new-program-name");
  try {
    await apiPost("/api/playlist/programs", {
      name: nameInput.value,
      defaultBackgroundArtId: newProgramBackgroundArtSelect.value || null,
      // Sprint GC-Campos-Globais (2026-11-19): o GC padrão deixou de ser perguntado na criação do
      // Programa — os valores padrão agora se definem depois, via "✎ GC padrão deste Programa"
      // (openProgramGcModal, abaixo), já com as caixinhas certas vindas de Configurações → GC.
      defaultGcFields: null,
    });
    nameInput.value = "";
    newProgramBackgroundArtSelect.value = "";
    await loadPrograms();
  } catch (err) {
    showError(err.message);
  }
});

// Sprint GC-Campos-Fixos (2026-11-17) — modal "GC padrão do Programa" (Admin), aberto a partir
// do botão em #editions-section (ver index.html).
// Sprint GC-Campos-Globais (2026-11-19, revisão): trocado o textarea livre "Nome: Valor" por
// caixinhas (uma por nome global de Configurações → GC, via gcFieldNamesCache) — mesmo padrão de
// renderLaudaGcFieldInputs/collectLaudaGcFieldsFromForm, só que aqui é VALOR PADRÃO do Programa,
// não valor de uma Lauda específica.
function renderProgramGcValueInputs(values) {
  const fieldNames = gcFieldNamesCache || [];
  programGcFieldsDynamicContainer.innerHTML = "";
  programGcHint.classList.toggle("hidden", fieldNames.length > 0);
  fieldNames.forEach((name) => {
    const row = document.createElement("div");
    row.className = "gc-field-row";
    const label = document.createElement("label");
    label.textContent = name;
    const input = document.createElement("input");
    input.type = "text";
    input.dataset.gcFieldName = name;
    input.value = (values && values[name]) || "";
    row.appendChild(label);
    row.appendChild(input);
    programGcFieldsDynamicContainer.appendChild(row);
  });
}

/// Inverso de renderProgramGcValueInputs — lê as caixinhas de volta pro dicionário nome→valor que
/// PUT /api/playlist/programs/gc-fields espera.
function collectProgramGcValuesFromForm() {
  const fields = {};
  programGcFieldsDynamicContainer.querySelectorAll("[data-gc-field-name]").forEach((input) => {
    fields[input.dataset.gcFieldName] = input.value;
  });
  return fields;
}

async function openProgramGcModal() {
  const program = currentPrograms.find((p) => p.id === selectedProgramId);
  await ensureGcFieldNames();
  renderProgramGcValueInputs(program ? program.defaultGcFields : null);
  programGcFormError.classList.add("hidden");
  programGcModal.classList.remove("hidden");
}

function closeProgramGcModal() {
  programGcModal.classList.add("hidden");
  programGcForm.reset();
}

programGcEditButton.addEventListener("click", openProgramGcModal);
programGcFormCancel.addEventListener("click", closeProgramGcModal);

programGcForm.addEventListener("submit", async (event) => {
  event.preventDefault();
  programGcFormError.classList.add("hidden");
  try {
    await apiPut("/api/playlist/programs/gc-fields", {
      programId: selectedProgramId,
      defaultGcFields: collectProgramGcValuesFromForm(),
    });
    closeProgramGcModal();
    // Recarrega o cache de Programas (currentPrograms) pra que o próximo "✎ GC padrão deste
    // Programa" já abra com os valores salvos.
    await loadPrograms();
  } catch (err) {
    programGcFormError.textContent = err.message;
    programGcFormError.classList.remove("hidden");
  }
});

document.getElementById("new-edition-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  clearError();
  const dateInput = document.getElementById("new-edition-date");
  try {
    await apiPost("/api/playlist/editions", { programId: selectedProgramId, date: dateInput.value });
    dateInput.value = "";
    await loadEditions();
  } catch (err) {
    showError(err.message);
  }
});

document.getElementById("back-button").addEventListener("click", () => {
  selectedProgramId = null;
  editionsSection.classList.add("hidden");
  programsSection.classList.remove("hidden");
});

document.getElementById("back-to-editions-button").addEventListener("click", () => {
  selectedEditionId = null;
  espelhoSection.classList.add("hidden");
  editionsSection.classList.remove("hidden");
  closeLaudaForm();
});

document.getElementById("toggle-new-lauda-button").addEventListener("click", () => {
  if (newLaudaForm.classList.contains("hidden")) {
    openLaudaCreateForm();
  } else {
    closeLaudaForm();
  }
});

document.getElementById("lauda-form-cancel").addEventListener("click", () => {
  closeLaudaForm();
});

/// Sprint 3 (2026-11-16): o mesmo formulário/botão agora serve tanto pra criar (POST, sem
/// editingLaudaId) quanto pra editar (PUT, com editingLaudaId setado por openLaudaForEdit) — ver
/// comentário de openLaudaForEdit pro porquê de reaproveitar em vez de duplicar o formulário.
document.getElementById("new-lauda-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  clearError();

  const titleInput = document.getElementById("new-lauda-title");
  const typeInput = document.getElementById("new-lauda-type");
  const durationInput = document.getElementById("new-lauda-duration");
  const tpInput = document.getElementById("new-lauda-tp");
  const offInput = document.getElementById("new-lauda-off");
  const notesInput = document.getElementById("new-lauda-notes");
  const vtInput = document.getElementById("new-lauda-vt");
  const imagesInput = document.getElementById("new-lauda-images");

  const laudaFields = {
    title: titleInput.value,
    type: typeInput.value,
    gcFields: collectLaudaGcFieldsFromForm(),
    tpText: tpInput.value || null,
    offText: offInput.value || null,
    durationPlanned: durationInput.value || null,
    notes: notesInput.value || null,
  };

  const isEditing = Boolean(editingLaudaId);
  laudaUploadStatus.classList.remove("hidden");
  laudaUploadStatus.textContent = isEditing ? "Salvando alterações..." : "Criando Lauda...";

  try {
    const lauda = isEditing
      ? await apiPut("/api/playlist/laudas", {
          laudaId: editingLaudaId,
          programId: selectedProgramId,
          editionId: selectedEditionId,
          ...laudaFields,
        })
      : await apiPost("/api/playlist/laudas", {
          programId: selectedProgramId,
          editionId: selectedEditionId,
          ...laudaFields,
        });

    const vtFile = vtInput.files[0];
    if (vtFile) {
      laudaUploadStatus.textContent = `Enviando VT (${vtFile.name})...`;
      const query = `${laudaQuery()}&laudaId=${encodeURIComponent(lauda.id)}&filename=${encodeURIComponent(vtFile.name)}`;
      await uploadFile(`/api/playlist/laudas/vt?${query}`, vtFile);
    }

    const imageFiles = Array.from(imagesInput.files || []);
    for (let i = 0; i < imageFiles.length; i++) {
      const imageFile = imageFiles[i];
      laudaUploadStatus.textContent = `Enviando imagem ${i + 1}/${imageFiles.length} (${imageFile.name})...`;
      const query = `${laudaQuery()}&laudaId=${encodeURIComponent(lauda.id)}&filename=${encodeURIComponent(imageFile.name)}`;
      await uploadFile(`/api/playlist/laudas/imagem?${query}`, imageFile);
    }

    laudaUploadStatus.classList.add("hidden");
    laudaUploadStatus.textContent = "";
    closeLaudaForm();
    await loadLaudas();
  } catch (err) {
    laudaUploadStatus.classList.add("hidden");
    showError(err.message);
  }
});

// ===================== Sprint Controle-Remoto-TP (2026-11-17) — Teleprompter remoto =====================
// Pedido do usuário: controlar o Teleprompter (desligar o automático/voz e digitar texto na mão)
// de OUTRO PC, não da própria máquina da Cartucheira — esta view + as rotas server-side
// (PlaylistDashboardCoordinator.HandleGetTeleprompterState/HandleSetTeleprompterManual/
// HandleSetTeleprompterText) são esse "outro software ou url". Sem tela de permissão por Programa
// (ver comentário de classe do PlaylistDashboardCoordinator) — qualquer usuário logado controla.
//
// Parte 2 (2026-11-17, pedido do usuário "2 segundos e muito, precisamos pensar outra
// alternativa"): o polling original de 2s virou um EventSource (Server-Sent Events) — o servidor
// (PlaylistSseChannel, do lado de PlaylistDashboardCoordinator.OnTeleprompterStateChanged) empurra
// o estado pra ESTA aba na hora que algo muda em QUALQUER lugar (outra aba, ou a própria Lauda
// carregada mudando automaticamente), sem esperar nenhum intervalo. A única consulta "pergunta-
// resposta" que resta é a de GET /api/teleprompter/state ao abrir a view, só pra ter alguma coisa
// na tela antes do EventSource terminar de conectar.
//
// Parte 3 (2026-11-17, pedido do usuário "essa tela que abriu no browser precisa ser um espelho
// das tela do tp [...] como se fosse um acesso remoto da tela e ai ter os controles de tp"):
// pesquisa sobre apps profissionais de teleprompter com controle remoto (RemoteCue, PromptSmart
// Pro, TeleCuePro) confirmou que a prática padrão NÃO é um vídeo literal da tela — é um espelho de
// TEXTO + POSIÇÃO DE LEITURA sincronizados, porque isso é visualmente idêntico, muito mais barato
// (nenhuma captura/encode/stream de vídeo, só os mesmos bytes de texto + um número de 0 a 1) e
// funciona em qualquer PC/celular sem plugin nenhum. #tp-mirror reproduz a aparência exata da
// janela real (TeleprompterOutputWindow.xaml: fundo preto, texto branco negrito Segoe UI, linha de
// referência azul a 35%) com scrollTop/transform calculados a partir de scrollFraction/fontSizePt/
// mirrored, todos vindos do mesmo estado já empurrado pelo SSE — nenhuma rota nova só pra isso.
// Os controles (Play/Pause+velocidade 1-10, passo manual, início) são o segundo achado da pesquisa:
// o controle mais básico/universal de qualquer teleprompter remoto profissional, confirmado com o
// usuário ("Sim, é isso" / "Escala simples 1-10") em vez de medir em palavras-por-minuto.

/// Sprint Controle-Remoto-TP, parte 3 (2026-11-17) — escala aproximada de pixels-de-fonte-CSS por
/// ponto de FontSizePt da janela real, só pro espelho web parecer proporcionalmente do mesmo
/// "tamanho" que a janela de verdade. Palpite honesto baseado numa largura de referência de 1920px
/// (a landing mais comum de monitor/TV de teleprompter nesta base, ver Theme.xaml) — não existe
/// medição real de DPI/monitor do lado do navegador, então isto é só uma aproximação visual, nunca
/// um valor que precise ser exato.
const TP_MIRROR_REFERENCE_WIDTH_PX = 1920;

/// Aplica um estado {manualOverride, currentText, scrollFraction, autoScrollPlaying,
/// autoScrollSpeed, fontSizePt, mirrored} na UI — chamado tanto pela carga inicial (GET) quanto por
/// qualquer evento recebido do EventSource, e direto pela resposta de uma ação (toggle/enviar
/// texto/rolar/Play/velocidade, que já chegam com o estado atualizado, sem precisar esperar o
/// evento do servidor).
function applyTeleprompterState(state) {
  tpManualState = state.manualOverride;
  tpManualToggle.checked = state.manualOverride;
  tpStatusBadge.textContent = state.manualOverride ? "Manual" : "Automático";
  tpStatusBadge.classList.toggle("manual", state.manualOverride);
  tpStatusBadge.classList.toggle("auto", !state.manualOverride);
  tpTextInput.disabled = !state.manualOverride;
  tpSendTextButton.disabled = !state.manualOverride;

  const controlsEnabled = state.manualOverride;
  tpJumpStartButton.disabled = !controlsEnabled;
  tpStepUpButton.disabled = !controlsEnabled;
  tpStepDownButton.disabled = !controlsEnabled;
  tpPlayPauseButton.disabled = !controlsEnabled;
  tpSpeedSlider.disabled = !controlsEnabled;

  // Não sobrescreve o que a pessoa está digitando agora (foco no campo) — só atualiza o texto
  // mostrado quando o campo não está em uso, pra não "brigar" com quem está editando.
  if (document.activeElement !== tpTextInput) {
    tpTextInput.value = state.currentText || "";
  }

  // Espelho: mesmo texto, posição de leitura sincronizada via scrollFraction (0..1) — ver
  // comentário da seção completo sobre o porquê deste ser um espelho de texto, não de vídeo.
  // translateY + scaleX combinados num único transform (em vez de margin-top + classe CSS
  // separada) pra evitar qualquer ordem de aplicação estranha entre os dois efeitos.
  tpMirrorText.textContent = state.currentText || "";
  const fontSizePx = (state.fontSizePt || 28) * (tpMirror.clientWidth / TP_MIRROR_REFERENCE_WIDTH_PX) * 1.333;
  tpMirrorText.style.fontSize = Math.max(10, fontSizePx) + "px";
  const scrollable = Math.max(0, tpMirrorText.scrollHeight - tpMirror.clientHeight);
  const translateY = -((state.scrollFraction || 0) * scrollable);
  const flip = state.mirrored ? " scaleX(-1)" : "";
  tpMirrorText.style.transform = `translateY(${translateY}px)${flip}`;

  // Play/Pause + velocidade — não sobrescreve o slider enquanto o usuário está arrastando (mesmo
  // cuidado do textarea acima).
  tpPlayPauseButton.textContent = state.autoScrollPlaying ? "⏸ Pause" : "▶ Play";
  tpPlayPauseButton.classList.toggle("playing", !!state.autoScrollPlaying);
  if (!tpSpeedDragging) {
    // Fix v1.0.102 (2026-11-17, "de 1 em 1 esta com muita diferença") — speed agora é um número
    // fracionário (0.1 em 0.1, não mais inteiro de 1 em 1); toFixed(1) pra sempre mostrar "5.0", não
    // às vezes "5" às vezes "4.7" (consistência visual).
    const speed = state.autoScrollSpeed || 5;
    tpSpeedSlider.value = speed;
    tpSpeedValue.textContent = speed.toFixed(1);
  }
}

async function loadTeleprompterState() {
  try {
    const state = await apiGet("/api/teleprompter/state");
    tpError.classList.add("hidden");
    applyTeleprompterState(state);
  } catch (err) {
    tpError.textContent = err.message;
    tpError.classList.remove("hidden");
  }
}

/// Sprint Controle-Remoto-TP, parte 2: abre a conexão de verdade (EventSource nativo do navegador,
/// cookie de sessão já vai sozinho porque é uma chamada same-origin) de novo a cada vez que a view
/// é aberta, fecha ao saltar pra outra view (ver navItems abaixo) — evita manter uma conexão eterna
/// em segundo plano em views que ninguém está olhando. GET inicial primeiro (não espera o
/// EventSource conectar pra mostrar alguma coisa), depois liga o stream pras próximas atualizações.
function startTeleprompterStream() {
  stopTeleprompterStream();
  loadTeleprompterState();

  tpEventSource = new EventSource("/api/teleprompter/stream");
  tpEventSource.addEventListener("state", (event) => {
    tpError.classList.add("hidden");
    applyTeleprompterState(JSON.parse(event.data));
  });
  tpEventSource.addEventListener("error", () => {
    // O EventSource do navegador já tenta reconectar sozinho (comportamento nativo, sem código
    // nenhum nosso) — só avisa na tela que a conexão caiu, pra não parecer que simplesmente parou
    // de atualizar sem motivo. Some sozinho no próximo evento "state" recebido com sucesso.
    tpError.textContent = "conexão com a Cartucheira caiu — tentando reconectar...";
    tpError.classList.remove("hidden");
  });
}

function stopTeleprompterStream() {
  if (tpEventSource !== null) {
    tpEventSource.close();
    tpEventSource = null;
  }
}

tpManualToggle.addEventListener("change", async () => {
  tpError.classList.add("hidden");
  const wanted = tpManualToggle.checked;
  tpManualToggle.disabled = true;
  try {
    const state = await apiPost("/api/teleprompter/manual", { enabled: wanted });
    applyTeleprompterState(state);
  } catch (err) {
    tpManualToggle.checked = tpManualState; // desfaz a marcação — a chamada falhou.
    tpError.textContent = err.message;
    tpError.classList.remove("hidden");
  } finally {
    tpManualToggle.disabled = false;
  }
});

tpSendTextButton.addEventListener("click", async () => {
  tpError.classList.add("hidden");
  try {
    const state = await apiPost("/api/teleprompter/text", { text: tpTextInput.value });
    applyTeleprompterState(state);
  } catch (err) {
    tpError.textContent = err.message;
    tpError.classList.remove("hidden");
  }
});

// Parte 3 (2026-11-17) — controles de rolagem remota. Mesmo padrão de tratamento de erro dos dois
// listeners acima (mostra em #tp-error, nunca trava a tela); nenhum desabilita/reabilita o próprio
// botão clicado (diferente do toggle manual) porque a resposta volta rápido o bastante (chamada
// local na rede do estúdio) pra não precisar desse cuidado extra.
tpJumpStartButton.addEventListener("click", async () => {
  tpError.classList.add("hidden");
  try {
    applyTeleprompterState(await apiPost("/api/teleprompter/jump-start", {}));
  } catch (err) {
    tpError.textContent = err.message;
    tpError.classList.remove("hidden");
  }
});

tpStepUpButton.addEventListener("click", async () => {
  tpError.classList.add("hidden");
  try {
    applyTeleprompterState(await apiPost("/api/teleprompter/scroll-step", { direction: "up" }));
  } catch (err) {
    tpError.textContent = err.message;
    tpError.classList.remove("hidden");
  }
});

tpStepDownButton.addEventListener("click", async () => {
  tpError.classList.add("hidden");
  try {
    applyTeleprompterState(await apiPost("/api/teleprompter/scroll-step", { direction: "down" }));
  } catch (err) {
    tpError.textContent = err.message;
    tpError.classList.remove("hidden");
  }
});

tpPlayPauseButton.addEventListener("click", async () => {
  tpError.classList.add("hidden");
  const wantedPlaying = tpPlayPauseButton.textContent.includes("Play"); // estado atual mostrado, não tpManualState (isto é independente do manual).
  try {
    applyTeleprompterState(await apiPost("/api/teleprompter/autoscroll/play", { playing: wantedPlaying }));
  } catch (err) {
    tpError.textContent = err.message;
    tpError.classList.remove("hidden");
  }
});

// Sprint Controle-Remoto-TP, parte 3 (2026-11-17) — "input" (dispara a cada arrasto, não só ao
// soltar) pra dar feedback imediato do número ao lado do slider; a chamada de rede em si só vai no
// "change" final (soltar o slider/clicar numa posição), pra não inundar o servidor de requisições
// a cada pixel arrastado.
tpSpeedSlider.addEventListener("input", () => {
  tpSpeedDragging = true;
  tpSpeedValue.textContent = Number(tpSpeedSlider.value).toFixed(1);
});

tpSpeedSlider.addEventListener("change", async () => {
  tpError.classList.add("hidden");
  try {
    applyTeleprompterState(await apiPost("/api/teleprompter/autoscroll/speed", { speed: Number(tpSpeedSlider.value) }));
  } catch (err) {
    tpError.textContent = err.message;
    tpError.classList.remove("hidden");
  } finally {
    tpSpeedDragging = false;
  }
});

// Arrastar a barra do espelho (#tp-mirror) pula direto pra essa posição do texto — scrubber, mesmo
// conceito de "clicar numa posição da barra de progresso" de qualquer player de mídia. Clique
// simples (sem arrastar) já funciona igual, por ser só um "mousedown" seguido de "click" sem
// movimento. Só ativo com o manual ligado (mesmo gate dos outros controles, ver
// applyTeleprompterState) — checagem feita aqui porque o próprio elemento nunca fica "disabled"
// (não é um <button>/<input>), diferente dos outros controles desta seção.
tpMirror.addEventListener("click", async (event) => {
  if (!tpManualState) {
    return;
  }

  tpError.classList.add("hidden");
  const rect = tpMirror.getBoundingClientRect();
  const fraction = Math.min(1, Math.max(0, (event.clientY - rect.top) / rect.height));
  try {
    applyTeleprompterState(await apiPost("/api/teleprompter/scroll-to", { fraction }));
  } catch (err) {
    tpError.textContent = err.message;
    tpError.classList.remove("hidden");
  }
});

// ===================== Sprint Playlist-TP (2026-11-17) — Playlist dentro do Teleprompter =====================
// Pedido do usuário: "no teleprompter, ele clique lá, carrega o playlist e traga as laudas de
// teleprompter daquele playlist [...] caso precise operar manual. Ele carrega os textos ali, vai
// clicando, ele vai jogando o texto ali onde vai ser exibido [...] a produção já fez todos os
// textos, dá para abrir e operar manual". Reaproveita as MESMAS rotas GET /api/playlist/programs,
// /editions, /laudas já usadas pela view "Programas" — nenhuma rota nova no backend. Estado
// (tpPlaylistProgramId/tpPlaylistEditionId) é independente do selectedProgramId/selectedEditionId
// da view Programas, de propósito (ver comentário onde são declarados).

function resetTeleprompterPlaylistPicker() {
  tpPlaylistProgramId = null;
  tpPlaylistEditionId = null;
  tpPlaylistEditionSelect.innerHTML = '<option value="">Escolha uma Edição...</option>';
  tpPlaylistEditionSelect.disabled = true;
  tpPlaylistLaudasList.innerHTML = "";
  tpPlaylistError.classList.add("hidden");
}

tpPlaylistToggleButton.addEventListener("click", async () => {
  const opening = tpPlaylistPanel.classList.contains("hidden");
  tpPlaylistPanel.classList.toggle("hidden", !opening);
  tpPlaylistToggleButton.textContent = opening ? "▾ Carregar texto de uma Playlist" : "▸ Carregar texto de uma Playlist";

  if (!opening) {
    return;
  }

  if (tpPlaylistProgramsCache !== null) {
    return; // já carregado numa abertura anterior desta mesma sessão de página.
  }

  tpPlaylistError.classList.add("hidden");
  try {
    tpPlaylistProgramsCache = await apiGet("/api/playlist/programs");
    tpPlaylistProgramSelect.innerHTML = '<option value="">Escolha um Programa...</option>';
    tpPlaylistProgramsCache.forEach((program) => {
      const option = document.createElement("option");
      option.value = program.id;
      option.textContent = program.name;
      tpPlaylistProgramSelect.appendChild(option);
    });
  } catch (err) {
    tpPlaylistProgramsCache = null;
    tpPlaylistError.textContent = err.message;
    tpPlaylistError.classList.remove("hidden");
  }
});

tpPlaylistProgramSelect.addEventListener("change", async () => {
  tpPlaylistProgramId = tpPlaylistProgramSelect.value || null;
  tpPlaylistEditionId = null;
  tpPlaylistEditionSelect.innerHTML = '<option value="">Escolha uma Edição...</option>';
  tpPlaylistLaudasList.innerHTML = "";
  tpPlaylistError.classList.add("hidden");

  if (!tpPlaylistProgramId) {
    tpPlaylistEditionSelect.disabled = true;
    return;
  }

  try {
    const editions = await apiGet(`/api/playlist/editions?programId=${encodeURIComponent(tpPlaylistProgramId)}`);
    editions.forEach((edition) => {
      const option = document.createElement("option");
      option.value = edition.id;
      option.textContent = edition.dateIso;
      tpPlaylistEditionSelect.appendChild(option);
    });
    tpPlaylistEditionSelect.disabled = false;
  } catch (err) {
    tpPlaylistError.textContent = err.message;
    tpPlaylistError.classList.remove("hidden");
  }
});

/// Mostra TODAS as Laudas da Edição (decisão confirmada com o usuário) — as sem texto de TP ficam
/// esmaecidas (.tp-playlist-item-empty) em vez de escondidas, pra manter a ordem/contexto do bloco
/// visível mesmo nas que não servem pra clicar aqui.
tpPlaylistEditionSelect.addEventListener("change", async () => {
  tpPlaylistEditionId = tpPlaylistEditionSelect.value || null;
  tpPlaylistLaudasList.innerHTML = "";
  tpPlaylistError.classList.add("hidden");

  if (!tpPlaylistEditionId) {
    return;
  }

  try {
    const laudas = await apiGet(`/api/playlist/laudas?programId=${encodeURIComponent(tpPlaylistProgramId)}&editionId=${encodeURIComponent(tpPlaylistEditionId)}`);
    tpPlaylistLaudasList.innerHTML = "";
    laudas
      .slice()
      .sort((a, b) => a.order - b.order)
      .forEach((lauda) => {
        const hasTpText = Boolean(lauda.tpText && lauda.tpText.trim().length > 0);
        const item = document.createElement("li");
        item.className = hasTpText ? "clickable" : "clickable tp-playlist-item-empty";
        item.textContent = `${lauda.order + 1}. ${lauda.title || "(sem título)"} — ${lauda.type || "sem tipo"}${hasTpText ? "" : " (sem texto de TP)"}`;
        item.addEventListener("click", () => {
          // Decisão confirmada com o usuário: só preenche a caixa, nunca envia sozinho — "Enviar
          // texto" (já existente) continua sendo o único jeito de pôr no ar de verdade, dá chance de
          // revisar antes. Lauda sem texto de TP preenche com vazio (nunca inventa um texto).
          tpTextInput.value = lauda.tpText || "";
        });
        tpPlaylistLaudasList.appendChild(item);
      });
  } catch (err) {
    tpPlaylistError.textContent = err.message;
    tpPlaylistError.classList.remove("hidden");
  }
});

// ===================== Sprint GC (2026-11-17) — GC remoto =====================
// Pedido do usuário: "o sistema cartucheira vai enviar para o VMIX O GC automaticamente, assim que
// o tp ou vt for ativado [...] temos que ter um controle parecido ao tp para ser controlado qd
// necessario, dessativa o auto e enva um GC manualmente caso necessário [...] tenho o
// monitoramento do que esta no ar, parecido com o tp" — mesma estrutura de três peças do
// Teleprompter remoto (status/manual, SSE em vez de polling, monitoramento ao vivo), só que sem
// nenhuma janela/voz: o "destino" é só um conjunto de campos nome→valor mandado pro Título do vMix
// (ver GcViewModel/PlaylistDashboardCoordinator.HandleGetGcState/HandleSetGcManual/HandleSetGcFields).
//
// Monitoramento: depois de pesquisar (ver GcSettingsSnapshot/GcFieldLayout) que a API do vMix NÃO
// tem como mandar uma imagem/preview ao vivo do Título pela rede (só salva arquivo localmente na
// máquina do vMix), perguntei ao usuário como seguir — escolheu "Mockup configurável": o operador
// desenha a posição aproximada de cada campo uma vez em Configurações → GC (retângulo de
// referência 1920x1080, mesma lógica do espelho do Teleprompter), e #gc-monitor abaixo desenha
// esses retângulos com o VALOR atual de cada campo, escalados pra largura real da tela. Nenhum
// campo cadastrado ainda (gcFieldLayout vazio) cai pra uma lista simples nome/valor (#gc-monitor-fallback).

/// Busca a lista de posições do mockup uma vez por abertura da view — não muda em tempo real
/// (precisaria reabrir a view depois de editar Configurações → GC pra ver a mudança, mesmo
/// critério "nunca inventa"/"sem polling desnecessário" do resto desta dashboard).
async function loadGcFieldLayout() {
  try {
    gcFieldLayout = await apiGet("/api/gc/layout");
  } catch (err) {
    gcFieldLayout = []; // falha honesta — cai pro fallback de lista simples.
  }
  // Sprint GC-Campos-Globais (2026-11-19): aproveita esta mesma chamada (GET /api/gc/layout) pra
  // manter gcFieldNamesCache atualizado também — evita um segundo fetch redundante toda vez que a
  // view GC abre, já que os dois vêm da mesma lista (ver comentário de gcFieldNamesCache).
  gcFieldNamesCache = gcFieldLayout.map((field) => field.name);
}

/// Desenha #gc-monitor (mockup, se gcFieldLayout tiver algo) ou #gc-monitor-fallback (lista simples
/// nome/valor, senão) com os campos de <paramref name="fields"/> — chamado a cada applyGcState.
function renderGcMonitor(fields) {
  const entries = Object.entries(fields || {});

  if (gcFieldLayout.length === 0) {
    gcMonitor.classList.add("hidden");
    gcMonitorFallback.classList.remove("hidden");
    gcMonitorFallback.innerHTML = "";
    if (entries.length === 0) {
      const empty = document.createElement("li");
      empty.className = "hint";
      empty.textContent = "Nada no ar agora.";
      gcMonitorFallback.appendChild(empty);
    } else {
      entries.forEach(([name, value]) => {
        const item = document.createElement("li");
        item.textContent = `${name}: ${value}`;
        gcMonitorFallback.appendChild(item);
      });
    }
    return;
  }

  gcMonitorFallback.classList.add("hidden");
  gcMonitor.classList.remove("hidden");
  gcMonitor.innerHTML = "";

  // Retângulo de referência 1920x1080 (ver comentário da seção) escalado pra largura real de
  // #gc-monitor — mesma ideia de TP_MIRROR_REFERENCE_WIDTH_PX, aplicada em X/Y/Largura/Altura/Fonte
  // em vez de só no tamanho da fonte.
  const scale = gcMonitor.clientWidth / 1920;
  gcMonitor.style.height = Math.round(1080 * scale) + "px";

  const fieldsByName = Object.fromEntries(entries);
  gcFieldLayout.forEach((layout) => {
    const box = document.createElement("div");
    box.className = "gc-field-box";
    box.style.left = Math.round(layout.x * scale) + "px";
    box.style.top = Math.round(layout.y * scale) + "px";
    box.style.width = Math.round(layout.width * scale) + "px";
    box.style.height = Math.round(layout.height * scale) + "px";
    box.style.fontSize = Math.max(8, Math.round((layout.fontSizePt || 28) * scale * 1.333)) + "px";
    box.textContent = fieldsByName[layout.name] || "";
    box.classList.toggle("empty", !fieldsByName[layout.name]);
    gcMonitor.appendChild(box);
  });
}

/// Sprint GC-Campos-Globais (2026-11-19, revisão do v1.0.105): monta uma caixinha de texto PRA CADA
/// nome cadastrado GLOBALMENTE em Configurações → GC (gcFieldNamesCache, mantido atualizado por
/// loadGcFieldLayout toda vez que a view GC abre) — mesmo princípio de renderLaudaGcFieldInputs.
/// Nenhum campo cadastrado ainda em Configurações → GC = lista vazia = só a dica (#gc-fields-hint).
function renderGcManualFieldInputs(values) {
  const fieldNames = gcFieldNamesCache || [];
  gcFieldsDynamicContainer.innerHTML = "";
  gcFieldsHint.classList.toggle("hidden", fieldNames.length > 0);
  fieldNames.forEach((name) => {
    const row = document.createElement("div");
    row.className = "gc-field-row";
    const label = document.createElement("label");
    label.textContent = name;
    const input = document.createElement("input");
    input.type = "text";
    input.dataset.gcFieldName = name;
    input.value = (values && values[name]) || "";
    input.disabled = !gcManualState;
    row.appendChild(label);
    row.appendChild(input);
    gcFieldsDynamicContainer.appendChild(row);
  });
}

/// Inverso de renderGcManualFieldInputs — lê as caixinhas de volta pro dicionário nome→valor que
/// POST /api/gc/fields espera (ver gcSendFieldsButton abaixo).
function collectGcManualFieldsFromForm() {
  const fields = {};
  gcFieldsDynamicContainer.querySelectorAll("[data-gc-field-name]").forEach((input) => {
    fields[input.dataset.gcFieldName] = input.value;
  });
  return fields;
}

/// Aplica um estado {manualOverride, fields, isShowingDefault, isOnAir} na UI — mesmo papel de
/// applyTeleprompterState.
function applyGcState(state) {
  gcManualState = state.manualOverride;
  gcManualToggle.checked = state.manualOverride;
  gcStatusBadge.textContent = state.manualOverride ? "Manual" : "Automático";
  gcStatusBadge.classList.toggle("manual", state.manualOverride);
  gcStatusBadge.classList.toggle("auto", !state.manualOverride);

  gcSendFieldsButton.disabled = !state.manualOverride;
  gcClearFieldsButton.disabled = !state.manualOverride;

  // Mesmo cuidado de "não brigar com quem está digitando" de applyTeleprompterState — só
  // redesenha as caixinhas quando o foco não está em nenhuma delas (evita apagar o que o operador
  // está digitando no meio de uma atualização via SSE).
  if (!gcFieldsDynamicContainer.contains(document.activeElement)) {
    renderGcManualFieldInputs(state.fields);
  }

  renderGcMonitor(state.fields);
}

async function loadGcState() {
  try {
    const state = await apiGet("/api/gc/state");
    gcError.classList.add("hidden");
    applyGcState(state);
  } catch (err) {
    gcError.textContent = err.message;
    gcError.classList.remove("hidden");
  }
}

/// Mesmo padrão de startTeleprompterStream/stopTeleprompterStream — GET inicial (+ layout do
/// mockup) primeiro, depois liga o EventSource pras próximas atualizações.
function startGcStream() {
  stopGcStream();
  loadGcFieldLayout().then(loadGcState);

  gcEventSource = new EventSource("/api/gc/stream");
  gcEventSource.addEventListener("state", (event) => {
    gcError.classList.add("hidden");
    applyGcState(JSON.parse(event.data));
  });
  gcEventSource.addEventListener("error", () => {
    gcError.textContent = "conexão com a Cartucheira caiu — tentando reconectar...";
    gcError.classList.remove("hidden");
  });
}

function stopGcStream() {
  if (gcEventSource !== null) {
    gcEventSource.close();
    gcEventSource = null;
  }
}

gcManualToggle.addEventListener("change", async () => {
  gcError.classList.add("hidden");
  const wanted = gcManualToggle.checked;
  gcManualToggle.disabled = true;
  try {
    applyGcState(await apiPost("/api/gc/manual", { enabled: wanted }));
  } catch (err) {
    gcManualToggle.checked = gcManualState; // desfaz a marcação — a chamada falhou.
    gcError.textContent = err.message;
    gcError.classList.remove("hidden");
  } finally {
    gcManualToggle.disabled = false;
  }
});

gcSendFieldsButton.addEventListener("click", async () => {
  gcError.classList.add("hidden");
  try {
    applyGcState(await apiPost("/api/gc/fields", { fields: collectGcManualFieldsFromForm() }));
  } catch (err) {
    gcError.textContent = err.message;
    gcError.classList.remove("hidden");
  }
});

gcClearFieldsButton.addEventListener("click", async () => {
  gcError.classList.add("hidden");
  try {
    applyGcState(await apiPost("/api/gc/fields", { fields: {} }));
  } catch (err) {
    gcError.textContent = err.message;
    gcError.classList.remove("hidden");
  }
});

// ===================== Sprint GC-Playlist (2026-11-18) — Playlist dentro do GC =====================
// Pedido do usuário depois de testar o v1.0.104: "vamos precisar ter as mesma funções que tem no
// tp para poder operar manual e monitorar o que esta no ar" — mesmo navegador Programa→Edição→
// Lauda do Teleprompter (ver bloco "Sprint Playlist-TP" acima), reaproveitando as MESMAS rotas
// GET /api/playlist/programs/editions/laudas — nenhuma rota nova. Decisão confirmada com o usuário
// (AskUserQuestion): ao clicar numa Lauda, os valores entram em CAIXINHAS FIXAS (uma por campo do
// Programa escolhido), não na antiga caixa de texto livre — mesma consistência do v1.0.104.

function resetGcPlaylistPicker() {
  gcPlaylistProgramId = null;
  gcPlaylistEditionId = null;
  gcPlaylistEditionSelect.innerHTML = '<option value="">Escolha uma Edição...</option>';
  gcPlaylistEditionSelect.disabled = true;
  gcPlaylistLaudasList.innerHTML = "";
  gcPlaylistError.classList.add("hidden");
  renderGcManualFieldInputs({});
}

gcPlaylistToggleButton.addEventListener("click", async () => {
  const opening = gcPlaylistPanel.classList.contains("hidden");
  gcPlaylistPanel.classList.toggle("hidden", !opening);
  gcPlaylistToggleButton.textContent = opening ? "▾ Carregar de uma Playlist" : "▸ Carregar de uma Playlist";

  if (!opening) {
    return;
  }

  // Sprint GC-Campos-Globais (2026-11-19): garante gcFieldNamesCache carregado antes de qualquer
  // clique numa Lauda poder precisar dele — normalmente já veio de loadGcFieldLayout (startGcStream),
  // isto só cobre a corrida rara de abrir o picker antes desse primeiro carregamento terminar.
  await ensureGcFieldNames();

  if (gcPlaylistProgramsCache !== null) {
    return; // já carregado numa abertura anterior desta mesma sessão de página.
  }

  gcPlaylistError.classList.add("hidden");
  try {
    gcPlaylistProgramsCache = await apiGet("/api/playlist/programs");
    gcPlaylistProgramSelect.innerHTML = '<option value="">Escolha um Programa...</option>';
    gcPlaylistProgramsCache.forEach((program) => {
      const option = document.createElement("option");
      option.value = program.id;
      option.textContent = program.name;
      gcPlaylistProgramSelect.appendChild(option);
    });
  } catch (err) {
    gcPlaylistProgramsCache = null;
    gcPlaylistError.textContent = err.message;
    gcPlaylistError.classList.remove("hidden");
  }
});

/// Sprint GC-Campos-Globais (2026-11-19): trocar de Programa não muda mais o ESQUEMA de campos
/// (sempre global, ver gcFieldNamesCache) — só limpa os valores digitados/carregados até aqui, pra
/// não ficar com valor de um show antigo esquecido numa caixinha enquanto o operador navega pra
/// outro Programa.
gcPlaylistProgramSelect.addEventListener("change", async () => {
  gcPlaylistProgramId = gcPlaylistProgramSelect.value || null;
  gcPlaylistEditionId = null;
  gcPlaylistEditionSelect.innerHTML = '<option value="">Escolha uma Edição...</option>';
  gcPlaylistLaudasList.innerHTML = "";
  gcPlaylistError.classList.add("hidden");
  renderGcManualFieldInputs({});

  if (!gcPlaylistProgramId) {
    gcPlaylistEditionSelect.disabled = true;
    return;
  }

  try {
    const editions = await apiGet(`/api/playlist/editions?programId=${encodeURIComponent(gcPlaylistProgramId)}`);
    editions.forEach((edition) => {
      const option = document.createElement("option");
      option.value = edition.id;
      option.textContent = edition.dateIso;
      gcPlaylistEditionSelect.appendChild(option);
    });
    gcPlaylistEditionSelect.disabled = false;
  } catch (err) {
    gcPlaylistError.textContent = err.message;
    gcPlaylistError.classList.remove("hidden");
  }
});

/// Mostra TODAS as Laudas da Edição (mesma decisão confirmada do Teleprompter) — as sem nenhum
/// campo de GC preenchido ficam esmaecidas (.tp-playlist-item-empty) em vez de escondidas.
gcPlaylistEditionSelect.addEventListener("change", async () => {
  gcPlaylistEditionId = gcPlaylistEditionSelect.value || null;
  gcPlaylistLaudasList.innerHTML = "";
  gcPlaylistError.classList.add("hidden");

  if (!gcPlaylistEditionId) {
    return;
  }

  try {
    const laudas = await apiGet(`/api/playlist/laudas?programId=${encodeURIComponent(gcPlaylistProgramId)}&editionId=${encodeURIComponent(gcPlaylistEditionId)}`);
    gcPlaylistLaudasList.innerHTML = "";
    laudas
      .slice()
      .sort((a, b) => a.order - b.order)
      .forEach((lauda) => {
        const hasGc = lauda.gcFields && Object.values(lauda.gcFields).some((value) => value && value.trim().length > 0);
        const item = document.createElement("li");
        item.className = hasGc ? "clickable" : "clickable tp-playlist-item-empty";
        item.textContent = `${lauda.order + 1}. ${lauda.title || "(sem título)"} — ${lauda.type || "sem tipo"}${hasGc ? "" : " (sem GC preenchido)"}`;
        item.addEventListener("click", () => {
          // Mesma decisão confirmada do Teleprompter: só preenche as caixinhas, nunca envia
          // sozinho — "Enviar GC" continua sendo o único jeito de pôr no ar de verdade.
          renderGcManualFieldInputs(lauda.gcFields || {});
        });
        gcPlaylistLaudasList.appendChild(item);
      });
  } catch (err) {
    gcPlaylistError.textContent = err.message;
    gcPlaylistError.classList.remove("hidden");
  }
});

// ===================== Sprint 6 (2026-11-16) — Usuários da Dashboard (Admin) =====================
// Antes só dava pra gerenciar usuário pela Cartucheira desktop (Configurações → Playlist); agora o
// próprio Admin também consegue direto pela dashboard web — mesmas rotas server-side
// (PlaylistDashboardCoordinator.HandleListUsers/HandleCreateUser/HandleUpdateUserPermissions/
// HandleResetUserPassword/HandleDeleteUser), todas 403 pra quem não é Admin mesmo que alguém force
// a chamada sem passar pela UI.

function roleLabel(user) {
  return user.isAdmin ? "Administrador" : "Produtor";
}

function programNamesFor(user) {
  if (user.isAdmin) return "Todos os Programas";
  if (!user.allowedProgramIds || user.allowedProgramIds.length === 0) return "Nenhum Programa liberado ainda";
  const names = user.allowedProgramIds.map((id) => {
    const match = currentPrograms.find((p) => p.id === id);
    return match ? match.name : id;
  });
  return names.join(", ");
}

async function loadUsers() {
  usersError.classList.add("hidden");
  try {
    // Garante que o checklist do modal e os nomes de Programa na lista usem dados atuais, mesmo se
    // o Admin entrou direto na view de Usuários sem passar pela de Programas antes.
    currentPrograms = await apiGet("/api/playlist/programs");
    const users = await apiGet("/api/users");
    renderUsers(users);
  } catch (err) {
    usersError.textContent = err.message;
    usersError.classList.remove("hidden");
  }
}

function renderUsers(users) {
  usersList.innerHTML = "";
  users.forEach((user) => {
    const item = document.createElement("li");
    item.className = "clickable";

    const main = document.createElement("div");
    main.className = "user-row-main";
    const name = document.createElement("span");
    name.className = "user-row-name";
    name.textContent = user.username;
    const badge = document.createElement("span");
    badge.className = `role-badge ${user.isAdmin ? "admin" : "producer"}`;
    badge.textContent = user.isAdmin ? "Admin" : "Produtor";
    main.appendChild(name);
    main.appendChild(badge);

    const programsLabel = document.createElement("span");
    programsLabel.className = "user-row-programs";
    programsLabel.textContent = programNamesFor(user);

    item.appendChild(main);
    item.appendChild(programsLabel);
    item.addEventListener("click", () => openUserEditModal(user));

    usersList.appendChild(item);
  });
}

function renderProgramsChecklist(selectedIds) {
  userProgramsChecklist.innerHTML = "";
  if (currentPrograms.length === 0) {
    const empty = document.createElement("p");
    empty.className = "checklist-empty";
    empty.textContent = "Nenhum Programa cadastrado ainda — crie um Programa primeiro.";
    userProgramsChecklist.appendChild(empty);
    return;
  }

  currentPrograms.forEach((program) => {
    const label = document.createElement("label");
    const checkbox = document.createElement("input");
    checkbox.type = "checkbox";
    checkbox.value = program.id;
    checkbox.checked = Boolean(selectedIds && selectedIds.includes(program.id));
    label.appendChild(checkbox);
    label.append(program.name);
    userProgramsChecklist.appendChild(label);
  });
}

function selectedProgramIdsFromChecklist() {
  return Array.from(userProgramsChecklist.querySelectorAll('input[type="checkbox"]:checked')).map((cb) => cb.value);
}

function updateProgramsRowVisibility() {
  userProgramsRow.classList.toggle("hidden", userIsAdminInput.checked);
}

function openUserCreateModal() {
  editingUserId = null;
  userForm.reset();
  userFormError.classList.add("hidden");
  userModalTitle.textContent = "Novo usuário";
  userPasswordLabel.textContent = "Senha";
  userPasswordInput.placeholder = "mínimo 4 caracteres";
  userPasswordInput.required = true;
  userRemoveButton.classList.add("hidden");
  userIsAdminInput.checked = false;
  renderProgramsChecklist([]);
  updateProgramsRowVisibility();
  userModal.classList.remove("hidden");
}

function openUserEditModal(user) {
  editingUserId = user.id;
  userForm.reset();
  userFormError.classList.add("hidden");
  userModalTitle.textContent = `Editar usuário — ${user.username}`;
  userUsernameInput.value = user.username;
  userUsernameInput.disabled = true; // Sprint 6: trocar nome de usuário não é suportado nesta sprint — UserStore não tem um "RenameUser".
  userPasswordLabel.textContent = "Nova senha (deixe em branco pra manter a atual)";
  userPasswordInput.placeholder = "deixe em branco pra não trocar";
  userPasswordInput.required = false;
  userRemoveButton.classList.remove("hidden");
  userIsAdminInput.checked = user.isAdmin;
  renderProgramsChecklist(user.allowedProgramIds);
  updateProgramsRowVisibility();
  userModal.classList.remove("hidden");
}

function closeUserModal() {
  editingUserId = null;
  userUsernameInput.disabled = false;
  userModal.classList.add("hidden");
}

addUserButton.addEventListener("click", openUserCreateModal);
userFormCancel.addEventListener("click", closeUserModal);
userIsAdminInput.addEventListener("change", updateProgramsRowVisibility);

userForm.addEventListener("submit", async (event) => {
  event.preventDefault();
  userFormError.classList.add("hidden");

  const isAdmin = userIsAdminInput.checked;
  const allowedProgramIds = isAdmin ? [] : selectedProgramIdsFromChecklist();

  try {
    if (editingUserId) {
      await apiPut("/api/users", { id: editingUserId, isAdmin, allowedProgramIds });
      if (userPasswordInput.value) {
        await apiPut("/api/users/password", { id: editingUserId, newPassword: userPasswordInput.value });
      }
    } else {
      await apiPost("/api/users", {
        username: userUsernameInput.value,
        password: userPasswordInput.value,
        isAdmin,
        allowedProgramIds,
      });
    }
    closeUserModal();
    await loadUsers();
  } catch (err) {
    userFormError.textContent = err.message;
    userFormError.classList.remove("hidden");
  }
});

userRemoveButton.addEventListener("click", async () => {
  if (!editingUserId) return;
  if (!window.confirm(`Remover o usuário "${userUsernameInput.value}"? Esta ação não pode ser desfeita.`)) {
    return;
  }

  try {
    await apiDelete(`/api/users?id=${encodeURIComponent(editingUserId)}`);
    closeUserModal();
    await loadUsers();
  } catch (err) {
    userFormError.textContent = err.message;
    userFormError.classList.remove("hidden");
  }
});

checkAuthAndInit();
