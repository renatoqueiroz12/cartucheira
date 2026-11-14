// Cartucheira — Playlist dashboard (Sprint 1). Só JS puro, sem framework/build — ver comentário de
// classe em PlaylistDashboardHost.cs pro porquê (projeto não usa Node/npm em lugar nenhum).

const programsSection = document.getElementById("programs-section");
const editionsSection = document.getElementById("editions-section");
const programsList = document.getElementById("programs-list");
const editionsList = document.getElementById("editions-list");
const selectedProgramName = document.getElementById("selected-program-name");
const errorMessage = document.getElementById("error-message");

let selectedProgramId = null;

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
      editionsList.appendChild(item);
    });
  } catch (err) {
    showError(err.message);
  }
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

loadPrograms();
