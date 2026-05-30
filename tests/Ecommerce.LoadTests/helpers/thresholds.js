/**
 * SLOs partages entre tous les scenarios.
 *
 * p(95) < 500ms  - 95 % des requetes repondent en moins de 500ms
 * p(99) < 1000ms - 99 % des requetes repondent en moins de 1s
 * rate  < 0.01   - moins de 1 % d erreurs HTTP
 */
export const defaultThresholds = {
    http_req_duration: ['p(95)<500', 'p(99)<1000'],
    http_req_failed:   ['rate<0.01'],
};

/**
 * SLOs assouplis pour les tests de stress (on cherche le point de rupture,
 * pas a valider les SLOs nominaux).
 */
export const stressThresholds = {
    http_req_duration: ['p(95)<2000'],
    http_req_failed:   ['rate<0.10'],
};

/**
 * SLOs tres larges pour les tests de recherche de limites.
 * L objectif est d observer le comportement au-dela des SLOs nominaux,
 * pas de les valider. k6 continue meme si les seuils sont franchis.
 *
 * On s interesse surtout aux metriques brutes dans le rapport final :
 *   - A quel palier (VU) le p95 franchit 500ms, 1s, 2s ?
 *   - A quel palier les premieres erreurs 5xx apparaissent ?
 *   - Le systeme se recupere-t-il apres le pic (p95 revient sous 500ms) ?
 */
export const breakingThresholds = {
    http_req_duration: ['p(95)<5000'],  // echec seulement si > 5s (crash total)
    http_req_failed:   ['rate<0.50'],   // echec seulement si > 50% d erreurs
};
