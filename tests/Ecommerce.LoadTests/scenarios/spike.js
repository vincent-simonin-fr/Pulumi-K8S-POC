/**
 * Spike - pic soudain a 300 VU simulant un flash sale.
 *
 * Objectif : valider la resilience face a un trafic imprevisible et brutal.
 * Verifie que le systeme se retablit apres le pic sans degradation persistante.
 *
 * Etapes :
 *   1  VU pendant 30s  (trafic minimal - etat stable avant le choc)
 *   1 -> 300 VU en 10s (pic brutal)
 *   300 VU pendant 1m  (maintien du pic)
 *   300 -> 1 VU en 10s (retour normal)
 *   1  VU pendant 1m   (verification recuperation)
 *
 * Ce qu on observe dans Grafana :
 *   - Dashboard Services    : spike d erreurs et de latence pendant le pic
 *   - Dashboard Kubernetes  : HPA reagit-il assez vite ? (warm-up ~30s)
 *   - Dashboard .NET Runtime: GC Gen2 sous la pression memoire
 *   - Dashboard PostgreSQL  : connexions actives vs max_connections
 *
 * Usage :
 *   k6 run tests/Ecommerce.LoadTests/scenarios/spike.js
 */

import { sleep }            from 'k6';
import { stressThresholds } from '../helpers/thresholds.js';
import { mixedWorkload, getHealth, fetchProducts } from '../helpers/endpoints.js';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:30080';

export const options = {
    stages: [
        { duration: '30s', target: 1   },  // etat stable
        { duration: '10s', target: 300 },  // pic brutal
        { duration: '1m',  target: 300 },  // maintien du pic
        { duration: '10s', target: 1   },  // retour normal
        { duration: '1m',  target: 1   },  // verification recuperation
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
    sleep(0.2);
}
