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

// Sprint Controle-Remoto-TP, parte 2 (2026-11-17, pedido do usuário "2 segundos e muito,
// precisamos pensar outra alternativa" ao polling de 2s da parte 1): tpEventSource é a conexão de
// Server-Sent Events (só existe enquanto a view "teleprompter-view" estiver aberta, ver
// showView/navItems abaixo) — o servidor empurra o estado na hora que algo muda, em vez desta
// página ficar perguntando de novo e de novo. tpManualState guarda o último "manualOverride"
// conhecido, pra decidir se o checkbox/textarea devem ficar habilitados sem esperar a resposta de
// uma ação do próprio usuário.
let tpEventSource = null;
let tpManualState = false;

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

function parseGcFields(text) {
  const fields = {};
  text.split("\n").forEach((line) => {
    const trimmed = line.trim();
    if (!trimmed) return;
    const separatorIndex = trimmed.indexOf(":");
    if (separatorIndex === -1) return;
    const name = trimmed.slice(0, separatorIndex).trim();
    const value = trimmed.slice(separatorIndex + 1).trim();
    if (name) fields[name] = value;
  });
  return fields;
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

/// Inverso de parseGcFields — Sprint 3 (2026-11-16), usado ao abrir uma Lauda existente pra editar,
/// pra voltar o dicionário gcFields pro mesmo formato "Nome: Valor" por linha que o campo espera.
function stringifyGcFields(fields) {
  if (!fields) return "";
  return Object.entries(fields)
    .map(([name, value]) => `${name}: ${value}`)
    .join("\n");
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

function openLaudaCreateForm() {
  editingLaudaId = null;
  resetLaudaForm();
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
function openLaudaForEdit(lauda) {
  editingLaudaId = lauda.id;
  resetLaudaForm();
  document.getElementById("new-lauda-title").value = lauda.title === "(sem título)" ? "" : lauda.title;
  document.getElementById("new-lauda-type").value = lauda.type || "";
  document.getElementById("new-lauda-duration").value = lauda.durationPlanned || "";
  document.getElementById("new-lauda-gc").value = stringifyGcFields(lauda.gcFields);
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
    // sozinha.
    stopTeleprompterStream();

    showView(viewId);

    if (viewId === "users-view") {
      await loadUsers();
    } else if (viewId === "teleprompter-view") {
      startTeleprompterStream();
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
    });
    nameInput.value = "";
    newProgramBackgroundArtSelect.value = "";
    await loadPrograms();
  } catch (err) {
    showError(err.message);
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
  const gcInput = document.getElementById("new-lauda-gc");
  const tpInput = document.getElementById("new-lauda-tp");
  const offInput = document.getElementById("new-lauda-off");
  const notesInput = document.getElementById("new-lauda-notes");
  const vtInput = document.getElementById("new-lauda-vt");
  const imagesInput = document.getElementById("new-lauda-images");

  const laudaFields = {
    title: titleInput.value,
    type: typeInput.value,
    gcFields: parseGcFields(gcInput.value),
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

/// Aplica um estado {manualOverride, currentText} na UI — chamado tanto pela carga inicial (GET)
/// quanto por qualquer evento recebido do EventSource, e direto pela resposta de uma ação (toggle/
/// enviar texto, que já chega com o estado atualizado, sem precisar esperar o evento do servidor).
function applyTeleprompterState(state) {
  tpManualState = state.manualOverride;
  tpManualToggle.checked = state.manualOverride;
  tpStatusBadge.textContent = state.manualOverride ? "Manual" : "Automático";
  tpStatusBadge.classList.toggle("manual", state.manualOverride);
  tpStatusBadge.classList.toggle("auto", !state.manualOverride);
  tpTextInput.disabled = !state.manualOverride;
  tpSendTextButton.disabled = !state.manualOverride;

  // Não sobrescreve o que a pessoa está digitando agora (foco no campo) — só atualiza o texto
  // mostrado quando o campo não está em uso, pra não "brigar" com quem está editando.
  if (document.activeElement !== tpTextInput) {
    tpTextInput.value = state.currentText || "";
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
