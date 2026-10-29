// Publicación exclusiva de reglas, sin comprobaciones de índices ni cambios IAM.
// Ejecutar solo con autorización del propietario y GOOGLE_APPLICATION_CREDENTIALS externo.
const fs = require('node:fs/promises');
const path = require('node:path');
const { createRequire } = require('node:module');
const dependency = createRequire(path.join(__dirname, '../tests/firebase/package.json'));
const { initializeApp, applicationDefault, deleteApp } = dependency('firebase-admin/app');
const { getSecurityRules } = dependency('firebase-admin/security-rules');

async function main() {
  if (!process.argv.includes('--publish')) throw new Error('Falta --publish; este script cambia las reglas reales.');
  const root = path.resolve(__dirname, '..');
  const source = (await fs.readFile(path.join(root, 'firestore.rules'), 'utf8')).replace(/^\uFEFF/, '');
  const app = initializeApp({ credential: applicationDefault(), projectId: 'tunapp-j27' });
  try {
    const rules = getSecurityRules(app);
    const previous = await rules.getFirestoreRuleset();
    const backup = path.join(root, 'artifacts', 'deploy');
    await fs.mkdir(backup, { recursive: true });
    await fs.writeFile(path.join(backup, `firestore-before-${Date.now()}.json`), JSON.stringify(previous, null, 2));
    await rules.releaseFirestoreRulesetFromSource(source);
    const deployed = await rules.getFirestoreRuleset();
    if (!deployed.source.some(file => file.content === source)) throw new Error('La fuente publicada no coincide con el archivo revisado.');
    console.log(JSON.stringify({ projectId: 'tunapp-j27', ruleset: deployed.name, verified: true }));
  } finally { await deleteApp(app); }
}
main().catch(error => { console.error(`No se completó la publicación: ${error.message}`); process.exitCode = 1; });
