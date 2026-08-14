# Planilla de validación — API Banco Credicoop (BIE)

Evidencia de Request / Response **real** de las funcionalidades implementadas en el Portal,
capturada contra el ambiente de homologación del banco.

- **Entorno:** Homologación
- **Base URL:** `https://homoapibccl.bancocredicoop.coop`
- **Empresa / adherente:** BARK S.A. — Nº de adherente `1399230` — CUIT emisor `20100794889`
- **CBU cuenta débito:** `1910044555004401995596`
- **Fecha de captura:** 12/08/2026
- **Funcionalidades:** (1) E-Cheques · (2) Conciliación (movimientos de cuenta)

> Los tokens (client_assertion / access_token) van **recortados** por seguridad. El valor
> literal completo está en el archivo de captura `00-token.txt`, por si el banco lo exige.

---

## 0) Autenticación (OAuth2 · client_credentials + private_key_jwt)

`POST /auth/realms/homologacion/protocol/openid-connect/token`

### Request  — `Content-Type: application/x-www-form-urlencoded`
```
grant_type=client_credentials
scope=cuentas beneficiarioEcheq echeqConFirma consultaCbuCvuAlias
client_assertion_type=urn:ietf:params:oauth:client-assertion-type:jwt-bearer
client_assertion=eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIyMDEwMDc5NDg4OS... [JWT RS256 recortado]
```
Claims del `client_assertion` (JWT firmado con la clave privada de la empresa):
`sub` / `iss` = `20100794889` (client_id) · `aud` = URL del token · `jti` (uuid) · `iat` / `nbf` / `exp` (vigencia 5 min).

### Response  `200 OK`
```json
{
  "access_token": "eyJhbGciOiJSUzI1NiIsInR5cCIgOiAiSldUIiwia2lkIiA6ICI5czB2... [Bearer JWT recortado]",
  "expires_in": 1800,
  "refresh_expires_in": 0,
  "token_type": "Bearer",
  "not-before-policy": 1674054804,
  "scope": "consultaCbuCvuAlias echeqConFirma beneficiarioEcheq offline_access cuentas"
}
```

---

# FUNCIONALIDAD 1 — E-CHEQUES

Header común: `Authorization: Bearer <access_token>` · `Content-Type: application/json`

## 1.1) Alta de beneficiario en la agenda — caso OK

`POST /api/echeq/v1/beneficiario`

### Request
```json
{
  "numeroAdherente": 1399230,
  "idOrigen": "83405297-d160-40cb-b5fd-1768f9688e28",
  "beneficiarios": [
    { "orden": "0", "documento": "30696719884", "documentoTipo": "CUIT" }
  ]
}
```

### Response  `200 OK`
```json
{
  "data": {
    "idOrigen": "83405297-d160-40cb-b5fd-1768f9688e28",
    "fechaAlta": { "formatter": "yyyy-MM-dd'T'HH:mm:ss.SSS", "value": "2026-05-22T13:07:27.770" }
  }
}
```

## 1.2) Alta de beneficiario — caso rechazo (no bancarizado)

`POST /api/echeq/v1/beneficiario`

### Request
```json
{
  "numeroAdherente": 1399230,
  "idOrigen": "4f82ff84-5612-45d7-9733-be333d99af91",
  "beneficiarios": [
    { "orden": "0", "documento": "30556889364", "documentoTipo": "CUIT" }
  ]
}
```

### Response  `400 Bad Request`
```json
{
  "error": { "codigo": "APIE-8011", "descripcion": "Beneficiario no bancarizado" }
}
```

## 1.3) Emisión de e-cheque

`POST /api/echeq/v1/ConFirma/emision`

### Request
```json
{
  "numeroAdherente": 1399230,
  "idOrigen": "eaca93c8-e091-4165-9c09-0eefb32aead6",
  "cbuCuentaDebito": "1910044555004401995596",
  "echeqs": [
    {
      "monto": "4999886.75",
      "fechaPago": "20260815",
      "motivoPago": "prov",
      "caracter": "1",
      "modo": "1",
      "beneficiarioNombre": "CONTENEDORES HUGO S.A.",
      "beneficiarioDocumentoTipo": "CUIT",
      "beneficiarioDocumento": "30689674751",
      "concepto": "VAR",
      "tipoCheque": "ECHD",
      "mails": ["sandra@contenedoreshugo.com.ar"],
      "numeroCheque": 72592256
    }
  ]
}
```

### Response  `200 OK`
```json
{
  "data": {
    "idOperacion": 33322756,
    "idOrigen": "eaca93c8-e091-4165-9c09-0eefb32aead6",
    "fechaEmision": { "formatter": "yyyy-MM-dd'T'HH:mm:ss.SSS", "value": "2026-05-22T13:07:32.663" },
    "cbuCuentaDebito": "1910044555004401995596",
    "estadoOperacion": { "descripcion": "Enviada a la firma" }
  }
}
```

## 1.4) Listado de e-cheques generados

`POST /api/echeq/v1/lista-cheques`

### Request
```json
{
  "numeroAdherente": 1399230,
  "idOrigen": "7bcadd66-6f1c-4e6d-b1e4-8ad1a26dc566",
  "filtro": {
    "gestion": "GENERADOS",
    "estado": "TODOS",
    "cbuEmisor": "1910044555004401995596",
    "fechaEmisionDesde": "20260801",
    "fechaEmisionHasta": "20260812",
    "pagina": 1,
    "limite": 20
  }
}
```

### Response  `200 OK`
```json
{
  "data": {
    "numeroAdherente": 1399230,
    "idOrigen": "7bcadd66-6f1c-4e6d-b1e4-8ad1a26dc566",
    "echeqs": [
      {
        "chequeId": "XJE27QXRM8O97MY",
        "numeroCheque": 29354270,
        "cmc7completo": "19104418782935427000440199559",
        "cmc7": {
          "codBanco": "191",
          "codSucursal": "044",
          "codPostal": "1878",
          "numeroCheque": "29354270",
          "numeroCuenta": "00440199559"
        },
        "estado": "EMITIDO-PENDIENTE",
        "bancoCodigo": "191",
        "bancoNombre": "BANCO CREDICOOP COOPERATIVO LIMITADO",
        "codPostal": "1878",
        "caracter": "A LA ORDEN",
        "fechaPago": { "formatter": "yyyy-MM-dd'T'HH:mm:ss", "value": "2026-08-30T00:00:00" },
        "fechaEmision": { "formatter": "yyyy-MM-dd'T'HH:mm:ss", "value": "2026-08-04T13:14:14" },
        "fechaUltModificacion": { "formatter": "yyyy-MM-dd'T'HH:mm:ss.SSS", "value": "2026-08-04T13:14:14.987" },
        "monto": "150",
        "emisorCuit": "20100794889",
        "emisorRazonSocial": "TITLE-1 00440199559",
        "totalEndosos": 0,
        "totalCesiones": 0,
        "motivoPago": "Prueba homologacion Kalia",
        "moneda": "ARS"
      }
    ],
    "totalCheques": 1
  }
}
```

---

# FUNCIONALIDAD 2 — CONCILIACIÓN (movimientos de cuenta)

Header común: `Authorization: Bearer <access_token>` · `Accept: application/json`

## 2.1) Listado de cuentas del adherente

`GET /api/cuentas/v1/listaCuentas`

### Request
*(sin cuerpo)*

### Response  `200 OK`
```json
{
  "clarifCuentas": [
    { "nroCuenta": "00440199559", "denominacionCuenta": "TITLE-1 00440199559", "saldo": 3904691.4,  "tipoCuenta": "CC", "moneda": "ARS", "CBU": "1910044555004401995596" },
    { "nroCuenta": "10440194998", "denominacionCuenta": "TITLE-1 10440194998", "saldo": 1493430.63, "tipoCuenta": "CA", "moneda": "ARS", "CBU": "1910044555104401949985" },
    { "nroCuenta": "10440206237", "denominacionCuenta": "TITLE-1 10440206237", "saldo": 884.65,     "tipoCuenta": "CA", "moneda": "ARS", "CBU": "1910044555104402062371" },
    { "nroCuenta": "20440206244", "denominacionCuenta": "TITLE-1 20440206244", "saldo": 14848.33,   "tipoCuenta": "CA", "moneda": "USD", "CBU": "1910044555204402062442" }
  ]
}
```

## 2.2) Movimientos de una cuenta en un rango

`GET /api/cuentas/v1/00440199559/movimientos?fechaDesde=20250501&fechaHasta=20250507&topeMovimientos=1000`

### Request
*(sin cuerpo)*

### Response  `200 OK`
```json
{
  "consMovCtas": [
    { "fecha": "20250507", "descripcion": "ENCABEZADO", "indDBCR": "", "monto": 3904691.4, "nroComprobante": "", "codOperativo": "IDC", "saldo": 3025562.43, "idTransaccion": "" },
    { "fecha": "20250507", "descripcion": "Impuesto Ley 25.413 Ali Gral s/Debitos", "indDBCR": "DB", "monto": 486.94, "nroComprobante": "", "codOperativo": "IDCC3", "saldo": 3025562.43, "idTransaccion": "" },
    { "fecha": "20250507", "descripcion": "Impuesto Ley 25.413 Ali Gral s/Creditos", "indDBCR": "DB", "monto": 2480.83, "nroComprobante": "", "codOperativo": "IDCC1", "saldo": 3026049.37, "idTransaccion": "" },
    { "fecha": "20250507", "descripcion": "Recaudacion ARBA - PBA", "indDBCR": "DB", "monto": 10336.79, "nroComprobante": "", "codOperativo": "BREBA", "saldo": 3028530.2, "idTransaccion": "" },
    { "fecha": "20250507", "descripcion": "Transf. Interbanking - Distinto Titular Ord.:30663205621-CAJA DE SEGUROS SA", "indDBCR": "CR", "monto": 346403.4, "nroComprobante": "569210", "codOperativo": "00974", "saldo": 3038866.99, "idTransaccion": "FT25127790007010" },
    { "fecha": "20250507", "descripcion": "Transf. Interbanking - Distinto Titular Ord.:30663205621-CAJA DE SEGUROS SA", "indDBCR": "CR", "monto": 67068, "nroComprobante": "569402", "codOperativo": "00974", "saldo": 2692463.59, "idTransaccion": "FT25127493814011" },
    { "fecha": "20250507", "descripcion": "Debito/Credito Aut-ARCA Recaud. Previs. AFIP-20100794889", "indDBCR": "DB", "monto": 43318.7, "nroComprobante": "794889", "codOperativo": "00429", "saldo": 2625395.59, "idTransaccion": "FT25127092842030" },
    { "fecha": "20250507", "descripcion": "Debito/Credito Automatico-Tarjeta Visa VISA-0841245651", "indDBCR": "DB", "monto": 27501, "nroComprobante": "245651", "codOperativo": "00415", "saldo": 2668714.29, "idTransaccion": "FT25127092842037" },
    { "fecha": "20250505", "descripcion": "Impuesto Ley 25.413 Ali Gral s/Debitos", "indDBCR": "DB", "monto": 588.62, "nroComprobante": "", "codOperativo": "IDCC3", "saldo": 2696215.29, "idTransaccion": "" },
    { "fecha": "20250505", "descripcion": "Compra Local con Tarjeta de Debito Tarj:2294 Comercio:FLORENCIO VARELA", "indDBCR": "DB", "monto": 38643.88, "nroComprobante": "075224", "codOperativo": "00230", "saldo": 2696803.91, "idTransaccion": "FT25125635075224" },
    { "fecha": "20250505", "descripcion": "Compra Local con Tarjeta de Debito Tarj:2294 Comercio:DI CAMPO", "indDBCR": "DB", "monto": 59460, "nroComprobante": "631860", "codOperativo": "00230", "saldo": 2735447.79, "idTransaccion": "FT25125419631860" }
  ]
}
```

> El primer registro (ENCABEZADO, `indDBCR` vacío) el Portal lo descarta: sólo procesa los
> movimientos con `indDBCR` = `DB` o `CR`.

---

## Resumen de operaciones validadas

| # | Funcionalidad | Operación | Método + Endpoint | Resultado |
|---|---|---|---|---|
| 0 | Autenticación | Obtención de token | `POST /auth/realms/homologacion/.../token` | 200 OK |
| 1.1 | E-Cheques | Alta beneficiario | `POST /api/echeq/v1/beneficiario` | 200 OK |
| 1.2 | E-Cheques | Alta beneficiario (rechazo) | `POST /api/echeq/v1/beneficiario` | 400 APIE-8011 |
| 1.3 | E-Cheques | Emisión | `POST /api/echeq/v1/ConFirma/emision` | 200 OK |
| 1.4 | E-Cheques | Listado generados | `POST /api/echeq/v1/lista-cheques` | 200 OK |
| 2.1 | Conciliación | Listado de cuentas | `GET /api/cuentas/v1/listaCuentas` | 200 OK |
| 2.2 | Conciliación | Movimientos | `GET /api/cuentas/v1/{cuenta}/movimientos` | 200 OK |
