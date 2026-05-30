/**
 * Stress - montee progressive pour trouver le point de rupture reel.
 *
 * Calibrage base sur le test spike precedent (300 VU, p95=398ms, 0% erreur) :
 * le systeme n etait pas en limite. Ce test pousse jusqu a 700 VU pour identifier
 * le palier ou le p95 franchit 500ms et ou les erreurs apparaissent.
 *
 * Etapes :
 *   0 → 100 VU en 1m   (echauffement, zone confortable connue)
 *   100 → 250 VU en 2m (approche du test precedent — 300 VU)
 *   250 → 400 VU en 3m (depassement du test spike — HPA sature, premier stress)
 *   400 → 550 VU en 3m (zone inconnue — chercher la degradation)
 *   550 → 700 VU en 3m (limite attendue — p95 > 1s ou erreurs > 1 %)
 *   700 → 0   en 2m    (recuperation)
 *
 * Observations attendues par palier dans Grafana :
 *   100 VU  : nominal, HPA stable
 *   250 VU  : HPA order-api → 3-4, gateway → 2-3
 *   400 VU  : HPA maxe (order-api 4/4, gateway 3/3), PgBouncer pression
 *   550 VU  : saturation CPU pods, p95 commence a grimper (500-800ms)
 *   700 VU  : point de rupture probable — erreurs 5xx ou p95 > 2s
 *
 * Sleep 0.1s au lieu de 0.5s : pression 5x plus forte par VU
 * (chaque VU genere ~7 req/s vs ~1.5/s avant).
 *
 * Usage :
 *   k6 run tests/Ecommerce.LoadTests/scenarios/stress.js
 */

import { sleep }             from 'k6';
import { breakingThresholds } from '../helpers/thresholds.js';
import { mixedWorkload, getHealth, fetchProducts } from '../helpers/endpoints.js';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:30080';

export const options = {
    stages: [
        { duration: '1m', target: 100 },  // echauffement
        { duration: '2m', target: 250 },  // zone connue
        { duration: '3m', target: 400 },  // depassement spike precedent
        { duration: '3m', target: 550 },  // zone inconnue
        { duration: '3m', target: 700 },  // limite attendue
        { duration: '2m', target: 0   },  // recuperation
    ],
    thresholds: breakingThresholds,
};

export function setup() {
    getHealth(BASE_URL);
    const products = fetchProducts(BASE_URL);
    return { products };
}

export default function (data) {
    mixedWorkload(BASE_URL, data.products);
    sleep(0.1); // pression maximale — 5x plus intense que le stress precedent
}
