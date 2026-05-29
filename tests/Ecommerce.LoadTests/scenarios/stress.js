/**
 * Stress - montee progressive jusqu a 200 VU pour trouver le point de rupture.
 *
 * Objectif : identifier a partir de quel niveau de charge les SLOs sont violes
 * et comment se comportent l HPA, le thread pool .NET et le pool PostgreSQL.
 *
 * Etapes :
 *   0 -> 50 VU  en 2 min
 *   50 -> 100 VU en 2 min
 *   100 -> 150 VU en 2 min
 *   150 -> 200 VU en 2 min
 *   200 -> 0 VU en 2 min  (recuperation)
 *
 * Ce qu on observe dans Grafana :
 *   - Dashboard Services    : a quel palier le P95 depasse 500ms
 *   - Dashboard .NET Runtime: saturation du thread pool (queue length > 0)
 *   - Dashboard PostgreSQL  : saturation du pool de connexions
 *   - Dashboard Kubernetes  : declenchement du HPA (desired > current)
 *
 * Usage :
 *   k6 run tests/Ecommerce.LoadTests/scenarios/stress.js
 */

import { sleep }            from 'k6';
import { stressThresholds } from '../helpers/thresholds.js';
import { mixedWorkload, getHealth, fetchProducts } from '../helpers/endpoints.js';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:30080';

export const options = {
    stages: [
        { duration: '2m', target: 50  },
        { duration: '2m', target: 100 },
        { duration: '2m', target: 150 },
        { duration: '2m', target: 200 },
        { duration: '2m', target: 0   },
    ],
    thresholds: stressThresholds,
};

export function setup() {
    getHealth(BASE_URL);
    const products = fetchProducts(BASE_URL);
    return { products };
}

export default function (data) {
    mixedWorkload(BASE_URL, data.products);
    sleep(0.5); // moins de pause qu en charge nominale - pression maximale
}
