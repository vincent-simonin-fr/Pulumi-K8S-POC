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
