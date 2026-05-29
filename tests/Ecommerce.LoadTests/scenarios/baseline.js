/**
 * Baseline - 1 utilisateur virtuel, 2 minutes.
 *
 * Objectif : valider les temps de reponse nominaux avant tout test de charge.
 * Lancer AVANT les autres scenarios pour etablir une reference.
 *
 * Usage :
 *   k6 run tests/Ecommerce.LoadTests/scenarios/baseline.js
 *   k6 run tests/Ecommerce.LoadTests/scenarios/baseline.js --env BASE_URL=https://wizzz.com
 */

import { sleep }             from 'k6';
import { defaultThresholds } from '../helpers/thresholds.js';
import { addToCart, getInventory, getHealth, fetchProducts } from '../helpers/endpoints.js';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:30080';

export const options = {
    vus:        1,
    duration:   '2m',
    thresholds: defaultThresholds,
};

/**
 * setup() s execute une seule fois avant le test.
 * On recupere les vrais IDs produits depuis l API plutot que de les coder en dur —
 * les UUIDs sont auto-generes au seed et changent a chaque reset de la base.
 */
export function setup() {
    getHealth(BASE_URL);
    const products = fetchProducts(BASE_URL);
    return { products };
}

export default function (data) {
    getInventory(BASE_URL);
    sleep(1);

    addToCart(BASE_URL, data.products);
    sleep(1);
}
