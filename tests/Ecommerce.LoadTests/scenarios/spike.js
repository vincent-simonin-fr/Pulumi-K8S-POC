/**
 * Spike - double pic brutal pour trouver la limite de resilience.
 *
 * Calibrage base sur le test spike precedent (300 VU, p95=398ms, 0% erreur) :
 * le systeme gerait confortablement 300 VU. Ce test teste deux pics successifs
 * a 600 puis 1 000 VU pour trouver le seuil d erreur et evaluer la recuperation.
 *
 * Etapes :
 *   5 VU pendant 30s         (baseline stable — etat repos)
 *   5 → 600 VU en 10s        (pic 1 : 2x le test precedent, brutal)
 *   600 VU pendant 1m30s     (maintien — HPA a le temps de reagir)
 *   600 → 1 000 VU en 10s   (pic 2 : 3.3x le test precedent, limite extreme)
 *   1 000 VU pendant 1m      (maintien limite — trouver le point de rupture)
 *   1 000 → 5 VU en 15s     (retour brusque)
 *   5 VU pendant 1m          (verification recuperation — pas de degradation persistante)
 *
 * Ce qu on observe :
 *   Pic 1 (600 VU) : HPA sature (4+3 pods), p95 grimpe — systeme tient-il ?
 *   Pic 2 (1000 VU): saturation complete — premier palier avec erreurs 5xx ?
 *   Recuperation   : le p95 revient-il sous 500ms apres le pic ?
 *
 * Sleep 0.1s : chaque VU genere ~7 req/s → 600 VU ≈ 4 200 req/s theoriques
 * (pression brute bien superieure au test precedent a 355 req/s reel).
 *
 * Usage :
 *   k6 run tests/Ecommerce.LoadTests/scenarios/spike.js
 */

import { sleep }              from 'k6';
import { breakingThresholds } from '../helpers/thresholds.js';
import { mixedWorkload, getHealth, fetchProducts } from '../helpers/endpoints.js';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:30080';

export const options = {
    stages: [
        { duration: '30s', target: 5    },  // baseline repos
        { duration: '10s', target: 600  },  // pic 1 — brutal (2x precedent)
        { duration: '1m30s', target: 600 }, // maintien pic 1
        { duration: '10s', target: 1000 },  // pic 2 — extreme (3.3x precedent)
        { duration: '1m',  target: 1000 },  // maintien limite
        { duration: '15s', target: 5    },  // retour brusque
        { duration: '1m',  target: 5    },  // verification recuperation
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
    sleep(0.1);
}
