// Mint an HS256 bearer token for the deploy compose E2E — the same shape the app's JwtBearer accepts
// (ValidateIssuer/Audience off, ValidateLifetime on, signing key = Authentication:Jwt:SymmetricKey). The `nameid`
// claim is what ICurrentUser reads (ClaimTypes.NameIdentifier maps to "nameid"). Usage: node mint-jwt.js <key> <userId>
const crypto = require("crypto");
const [, , key, userId] = process.argv;
if (!key || !userId) { console.error("usage: node mint-jwt.js <symmetric-key> <user-guid>"); process.exit(1); }

const enc = (o) => Buffer.from(JSON.stringify(o)).toString("base64url");
const now = Math.floor(Date.now() / 1000);
const data = enc({ alg: "HS256", typ: "JWT" }) + "." + enc({ nameid: userId, nbf: now - 30, iat: now, exp: now + 3600 });
const sig = crypto.createHmac("sha256", key).update(data).digest("base64url");
process.stdout.write(data + "." + sig);
