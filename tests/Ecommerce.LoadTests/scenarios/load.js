/**
 * Load - montee progressive jusqu a 50 VU, maintien 5 minutes.
 *
 * Objectif : valider le comportement nominal sous charge realiste.
 * Charge mixte 80/20 (lecture inventaire / ajout panier).
 *
 * Etapes :
 *   0 -> 50 VU en 2 min  (warm-up)
 *   50 VU pendant 5 min  (charge soutenue)
 *   50 -> 0 VU en 1 min  (cool-down)
 *
 * Usage :
 *   k6 run tests/Ecommerce.LoadTests/scenarios/load.js
 *   k6 run tests/Ecommerce.LoadTests/scenarios/load.js --env BASE_URL=https://wizzz.com
 *
 * Avec export Prometheus (metriques visibles dans Grafana) :
 *   K6_PROMETHEUS_RW_SERVER_URL=http://localhost:9090/api/v1/write \
 *   k6 run --out experimental-prometheus-rw tests/Ecommerce.LoadTests/scenarios/load.js
 */

import { sleep }             from 'k6';
import { defaultThresholds } from '../helpers/thresholds.js';
import { mixedWorkload, getHealth, fetchProducts } from '../helpers/endpoints.js';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:30080';

export const options = {
    stages: [
        { duration: '2m', target: 50 },   // montee progressive
        { duration: '5m', target: 50 },   // charge soutenue
        { duration: '1m', target: 0  },   // descente
    ],
    thresholds: defaultThresholds,
};

export function setup() {
    getHealth(BASE_URL);
    const products = fetchProducts(BASE_URL);
    return { products };
}

export default function (data) {
    mixedWorkload(BASE_URL, data.products);
    sleep(Math.random() * 2 + 0.5); // pause realiste 0.5-2.5s entre requetes
}
