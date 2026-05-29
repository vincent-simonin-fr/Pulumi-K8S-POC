import { check, group } from 'k6';
import http             from 'k6/http';

const HEADERS = { 'Content-Type': 'application/json' };

// ── Helpers ────────────────────────────────────────────────────────────────────
function randomItem(arr) {
    return arr[Math.floor(Math.random() * arr.length)];
}

function randomQuantity(min = 1, max = 3) {
    return Math.floor(Math.random() * (max - min + 1)) + min;
}

function randomCustomerId() {
    // Genere un UUID v4 valide (hex uniquement : 0-9, a-f)
    return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, c => {
        const r = Math.random() * 16 | 0;
        return (c === 'x' ? r : (r & 0x3 | 0x8)).toString(16);
    });
}

// ── Bootstrap ─────────────────────────────────────────────────────────────────

/**
 * Recupere la liste des produits reels depuis GET /inventory.
 * A appeler dans setup() — le resultat est injecte dans default(data).
 *
 * Le catalogue provient du InventorySeeder (IDs auto-generes en base),
 * donc les IDs ne peuvent pas etre codes en dur dans le test.
 *
 * @returns {{ productId: string, productName: string, unitPrice: number }[]}
 */
export function fetchProducts(baseUrl) {
    const res = http.get(`${baseUrl}/inventory`);
    check(res, { 'inventory reachable': r => r.status === 200 });

    try {
        // Reponse : [{ id, name, sku, stockQuantity, reservedQuantity, availableQuantity }]
        return JSON.parse(res.body).map(p => ({
            productId:   p.id,
            productName: p.name,
            unitPrice:   9.99,   // prix non expose par l API, valeur fixe arbitraire > 0
        }));
    } catch {
        console.error('fetchProducts: impossible de parser la reponse inventory');
        return [];
    }
}

// ── Endpoints ──────────────────────────────────────────────────────────────────

/**
 * POST /orders — ajout au panier.
 * Traverse toute la stack : gateway → order-api → PostgreSQL + RabbitMQ → inventory-api.
 *
 * @param {string} baseUrl
 * @param {{ productId: string, productName: string, unitPrice: number }[]} products
 *   Liste retournee par fetchProducts() — passee via data depuis setup().
 */
export function addToCart(baseUrl, products) {
    if (!products || products.length === 0) {
        console.error('addToCart: liste de produits vide — appeler fetchProducts() dans setup()');
        return;
    }

    const product = randomItem(products);
    const payload = JSON.stringify({
        customerId:  randomCustomerId(),
        productId:   product.productId,
        productName: product.productName,
        unitPrice:   product.unitPrice,
        quantity:    randomQuantity(),
    });

    let res;
    group('POST /orders', () => {
        res = http.post(`${baseUrl}/orders`, payload, { headers: HEADERS });
        check(res, {
            'status 201': r => r.status === 201,
            'cartId present': r => {
                try { return JSON.parse(r.body).cartId !== undefined; }
                catch { return false; }
            },
        });
    });
    return res;
}

/**
 * GET /inventory — liste des produits avec stock.
 * Traverse : gateway → inventory-api → PostgreSQL.
 */
export function getInventory(baseUrl) {
    let res;
    group('GET /inventory', () => {
        res = http.get(`${baseUrl}/inventory`);
        check(res, {
            'status 200':    r => r.status === 200,
            'body non vide': r => r.body && r.body.length > 2,
        });
    });
    return res;
}

/**
 * GET /health — health check gateway.
 * Utilise dans setup() pour valider que le cluster est pret.
 */
export function getHealth(baseUrl) {
    const res = http.get(`${baseUrl}/health`);
    check(res, { 'gateway healthy': r => r.status === 200 });
    return res;
}

/**
 * Charge mixte realiste : 80 % GET /inventory, 20 % POST /orders.
 *
 * @param {string} baseUrl
 * @param {{ productId: string, productName: string, unitPrice: number }[]} products
 */
export function mixedWorkload(baseUrl, products) {
    if (Math.random() < 0.8) {
        getInventory(baseUrl);
    } else {
        addToCart(baseUrl, products);
    }
}
