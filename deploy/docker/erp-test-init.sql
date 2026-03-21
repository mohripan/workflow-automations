-- ERP Inventory test database
-- Used for testing the FlowForge SQL trigger

CREATE TABLE locations (
    id      SERIAL PRIMARY KEY,
    name    VARCHAR(100) NOT NULL,
    address TEXT
);

CREATE TABLE products (
    id                  SERIAL PRIMARY KEY,
    sku                 VARCHAR(50)  UNIQUE NOT NULL,
    name                VARCHAR(200) NOT NULL,
    quantity            INTEGER      NOT NULL DEFAULT 0,
    location_id         INTEGER      REFERENCES locations(id),
    reorder_threshold   INTEGER      NOT NULL DEFAULT 5,
    updated_at          TIMESTAMPTZ  NOT NULL DEFAULT NOW()
);

-- Locations
INSERT INTO locations (name, address) VALUES
    ('Warehouse A', '123 Industrial Blvd, Chicago, IL'),
    ('Warehouse B', '456 Commerce St, Houston, TX');

-- Products — all quantities start ABOVE 5 so the trigger is silent at boot
INSERT INTO products (sku, name, quantity, location_id, reorder_threshold) VALUES
    ('SKU-001', 'Industrial Bolt M8x30',   150,  1, 5),
    ('SKU-002', 'Steel Washer 8mm',        200,  1, 5),
    ('SKU-003', 'Hydraulic Pump P200',      12,  2, 5),
    ('SKU-004', 'Control Valve CV-10',       8,  2, 5),
    ('SKU-005', 'Pressure Gauge 100PSI',    25,  1, 5);
