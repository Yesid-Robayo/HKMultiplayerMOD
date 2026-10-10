#!/usr/bin/env bash
# Ajustes de seguridad del repositorio en GitHub (con gh, ya iniciado con una cuenta con permiso de administrador).
# Se puede ejecutar las veces que haga falta. Algunos ajustes GitHub solo los deja en repositorios públicos (gratis):
# protección de main, escaneo de secretos y aviso privado de vulnerabilidades. Ejecutar otra vez al hacerlo público.
#   - Sin wiki, proyectos ni debates; issues sí (para avisar de errores).
#   - main protegido: nadie puede borrarlo ni reescribir su historial (force push).
#   - Avisos de dependencias con fallos (Dependabot), escaneo de secretos con bloqueo al subir.
#   - Aviso privado de vulnerabilidades (pestaña Security).
#   - GitHub Actions: permisos de solo lectura por defecto y aprobación para los workflows de cualquier persona de fuera.
#   - Los pull requests de otros los cierra .github/workflows/close-pull-requests.yml (GitHub no deja desactivarlos).
# Uso: bash tools/github-setup.sh [usuario/repositorio]
set -uo pipefail

REPO="${1:-$(gh repo view --json nameWithOwner -q .nameWithOwner)}"
[ -n "$REPO" ] || { echo "github-setup.sh: no sé qué repositorio configurar" >&2; exit 1; }
VISIBILITY="$(gh repo view "$REPO" --json visibility -q .visibility)"
echo "Repositorio: $REPO ($VISIBILITY)"
failed=0
try() {  # descripción, orden...
  local what="$1"; shift
  local out
  if out="$("$@" 2>&1)"; then
    echo "  hecho: $what"
  else
    echo "  NO se pudo: $what"
    echo "$out" | grep -oE '"message":"[^"]*"' | head -n1 | sed 's/^/    /'
    failed=$((failed + 1))
  fi
}

try "sin wiki, proyectos ni debates; issues activados; borrar ramas al juntar" \
  gh repo edit "$REPO" --enable-wiki=false --enable-projects=false --enable-discussions=false --enable-issues=true \
  --delete-branch-on-merge=true

try "avisos de dependencias con fallos (Dependabot)" gh api -X PUT "repos/$REPO/vulnerability-alerts"

try "escaneo de secretos y bloqueo al subir un secreto" gh api -X PATCH "repos/$REPO" --input - <<'JSON'
{"security_and_analysis": {"secret_scanning": {"status": "enabled"},
                           "secret_scanning_push_protection": {"status": "enabled"}}}
JSON

try "aviso privado de vulnerabilidades" gh api -X PUT "repos/$REPO/private-vulnerability-reporting"

try "Actions: permisos de solo lectura por defecto" gh api -X PUT "repos/$REPO/actions/permissions/workflow" \
  -f default_workflow_permissions=read -F can_approve_pull_request_reviews=false

try "Actions: los workflows de cualquier persona de fuera esperan aprobación" \
  gh api -X PUT "repos/$REPO/actions/permissions/fork-pr-contributor-approval" -f approval_policy=all_external_contributors

# Protección de main con un conjunto de reglas (se actualiza si ya existe)
RULESET_NAME="Proteger main"
existing="$(gh api "repos/$REPO/rulesets" -q ".[] | select(.name == \"$RULESET_NAME\") | .id" 2>/dev/null || true)"
ruleset_json='{"name": "Proteger main", "target": "branch", "enforcement": "active",
  "conditions": {"ref_name": {"include": ["~DEFAULT_BRANCH"], "exclude": []}},
  "rules": [{"type": "deletion"}, {"type": "non_fast_forward"}]}'
if [ -n "$existing" ]; then
  try "main protegido (no borrar, no force push)" gh api -X PUT "repos/$REPO/rulesets/$existing" --input - <<<"$ruleset_json"
else
  try "main protegido (no borrar, no force push)" gh api -X POST "repos/$REPO/rulesets" --input - <<<"$ruleset_json"
fi

echo
if [ "$failed" -gt 0 ]; then
  echo "$failed ajuste(s) sin aplicar. Si el repositorio es privado, es normal: vuelve a ejecutar esto al hacerlo público."
else
  echo "Todo aplicado."
fi
