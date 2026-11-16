// Cartucheira — Playlist dashboard (Sprint 1). Só JS puro, sem framework/build — ver comentário de
// classe em PlaylistDashboardHost.cs pro porquê (projeto não usa Node/npm em lugar nenhum).

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

let selectedProgramId = null;
let selectedProgramName_ = null;
let selectedEditionId = null;

// Sprint 3 (2026-11-16): cache da última lista de Laudas carregada — usado pra calcular a nova
// ordem ao arrastar-e-soltar (reorderLaudas) e pra achar os dados completos de uma Lauda ao
// clicar nela pra editar (openLaudaForEdit), sem precisar de outra ida ao servidor pra cada clique.
let currentLaudas = [];

// Id da Lauda sendo editada no formulário — null quando o formulário está em modo "Nova Lauda".
let editingLaudaId = null;

// Id da Lauda sendo arrastada no momento (drag-and-drop nativo HTML5) — ver renderLaudas.
let draggedLaudaId = null;

function showError(message) {
  errorMessage.textContent = message;
  errorMessage.classList.remove("hidden");
}

function clearError() {
  errorMessage.classList.add("hidden");
  errorMessage.textContent = "";
}

async function apiGet(path) {
  const response = await fetch(path);
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
  if (!response.ok) {
    const text = await response.text();
    throw new Error(text || `Falha ao salvar (${response.status}).`);
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
  if (!response.ok) {
    const text = await response.text();
    throw new Error(text || `Falha ao enviar arquivo (${response.status}).`);
  }
  return response.json();
}

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

loadPrograms();
loadBackgroundArtPresets();
