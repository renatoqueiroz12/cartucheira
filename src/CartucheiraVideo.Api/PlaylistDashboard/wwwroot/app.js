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

let selectedProgramId = null;
let selectedProgramName_ = null;
let selectedEditionId = null;

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

async function loadLaudas() {
  clearError();
  try {
    const laudas = await apiGet(`/api/playlist/laudas?${laudaQuery()}`);
    laudasList.innerHTML = "";
    laudas.forEach((lauda) => {
      const item = document.createElement("li");
      const title = document.createElement("div");
      title.className = "lauda-title";
      title.textContent = `${lauda.order + 1}. ${lauda.title}${lauda.type ? " — " + lauda.type : ""}`;
      const details = document.createElement("div");
      details.className = "lauda-details";
      const durationText = lauda.durationPlanned ? `Duração prevista: ${lauda.durationPlanned} · ` : "";
      details.textContent = `${durationText}${describeLauda(lauda)}`;
      item.appendChild(title);
      item.appendChild(details);
      laudasList.appendChild(item);
    });
  } catch (err) {
    showError(err.message);
  }
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
    await apiPost("/api/playlist/programs", { name: nameInput.value });
    nameInput.value = "";
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
});

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

  laudaUploadStatus.classList.remove("hidden");
  laudaUploadStatus.textContent = "Criando Lauda...";

  try {
    const lauda = await apiPost("/api/playlist/laudas", {
      programId: selectedProgramId,
      editionId: selectedEditionId,
      title: titleInput.value,
      type: typeInput.value,
      gcFields: parseGcFields(gcInput.value),
      tpText: tpInput.value || null,
      offText: offInput.value || null,
      durationPlanned: durationInput.value || null,
      notes: notesInput.value || null,
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

    event.target.reset();
    laudaUploadStatus.classList.add("hidden");
    laudaUploadStatus.textContent = "";
    await loadLaudas();
  } catch (err) {
    laudaUploadStatus.classList.add("hidden");
    showError(err.message);
  }
});

loadPrograms();
