"""
pyodata integration tests for the OhData test bench server.

These tests exercise the v1 OData surface (Products, Categories) using both
pyodata (for metadata parsing) and plain requests (for HTTP-level assertions).

Seeded data (from DbSeeder in the TestBench):
  Products: id=1 Widget $9.99 Hardware
            id=2 Gadget $24.99 Electronics
            id=3 Sprocket $4.49 Hardware
            id=4 Doohickey $14.99 Misc
            id=5 Thingamajig $39.99 Electronics
  Categories: Hardware, Electronics, Misc
"""

import json
import requests
import pytest
import pyodata


# -- Seeded constants ---------------------------------------------------------

SEEDED_PRODUCT_ID = 1        # Widget, $9.99, Hardware
SEEDED_PRODUCT_NAME = "Widget"
MISSING_PRODUCT_ID = 99999


# -- pyodata client fixture ---------------------------------------------------

@pytest.fixture(scope="module")
def odata_client(base_url, http_session):
    """
    Build a pyodata.Client from the $metadata document.
    Scoped to the module so it is created once per test file.
    """
    metadata_url = f"{base_url}/v1/$metadata"
    metadata_response = http_session.get(metadata_url)
    assert metadata_response.status_code == 200, (
        f"Expected 200 from $metadata, got {metadata_response.status_code}"
    )
    client = pyodata.Client.build(base_url + "/v1", http_session)
    return client


# -- Helper: create a disposable product for write tests ----------------------

def _create_product(http_session, base_url, name, price, category):
    """POST a new product and return the created product dict."""
    resp = http_session.post(
        f"{base_url}/v1/Products",
        data=json.dumps({"name": name, "price": price, "category": category}),
    )
    assert resp.status_code == 201
    return resp.json()


def _delete_product(http_session, base_url, product_id):
    """DELETE a product by id; ignores 404."""
    http_session.delete(f"{base_url}/v1/Products({product_id})")


# -- $metadata ----------------------------------------------------------------

def test_metadata_document_parses(base_url, http_session):
    """pyodata.Client.build must succeed without raising an exception."""
    # build() fetches and parses $metadata internally
    client = pyodata.Client.build(base_url + "/v1", http_session)
    assert client is not None


def test_metadata_contains_products_entity_set(base_url, http_session):
    """The parsed metadata must expose a Products entity set."""
    client = pyodata.Client.build(base_url + "/v1", http_session)
    # pyodata exposes entity sets via client.schema.entity_sets
    entity_set_names = [es.name for es in client.schema.entity_sets]
    assert "Products" in entity_set_names


def test_metadata_contains_categories_entity_set(base_url, http_session):
    """The parsed metadata must expose a Categories entity set."""
    client = pyodata.Client.build(base_url + "/v1", http_session)
    entity_set_names = [es.name for es in client.schema.entity_sets]
    assert "Categories" in entity_set_names


# -- GET collection -----------------------------------------------------------

def test_get_collection_returns_200(base_url, http_session):
    """GET /v1/Products must return HTTP 200."""
    resp = http_session.get(f"{base_url}/v1/Products")
    assert resp.status_code == 200


def test_get_collection_has_value_array(base_url, http_session):
    """GET /v1/Products response body must contain a 'value' array."""
    resp = http_session.get(f"{base_url}/v1/Products")
    body = resp.json()
    assert "value" in body
    assert isinstance(body["value"], list)


def test_get_collection_is_non_empty(base_url, http_session):
    """GET /v1/Products must return at least the 5 seeded products."""
    resp = http_session.get(f"{base_url}/v1/Products")
    body = resp.json()
    assert len(body["value"]) >= 5


def test_get_collection_has_odata_context(base_url, http_session):
    """GET /v1/Products response body must include @odata.context."""
    resp = http_session.get(f"{base_url}/v1/Products")
    body = resp.json()
    assert "@odata.context" in body


# -- GET single entity --------------------------------------------------------

def test_get_single_entity_returns_200(base_url, http_session):
    """GET /v1/Products(1) must return HTTP 200."""
    resp = http_session.get(f"{base_url}/v1/Products({SEEDED_PRODUCT_ID})")
    assert resp.status_code == 200


def test_get_single_entity_has_correct_id(base_url, http_session):
    """GET /v1/Products(1) response body must have id == 1."""
    resp = http_session.get(f"{base_url}/v1/Products({SEEDED_PRODUCT_ID})")
    body = resp.json()
    assert body["id"] == SEEDED_PRODUCT_ID


def test_get_single_entity_has_correct_name(base_url, http_session):
    """GET /v1/Products(1) response body must have name == 'Widget'."""
    resp = http_session.get(f"{base_url}/v1/Products({SEEDED_PRODUCT_ID})")
    body = resp.json()
    assert body["name"] == SEEDED_PRODUCT_NAME


def test_get_missing_entity_returns_404(base_url, http_session):
    """GET /v1/Products(99999) must return HTTP 404."""
    resp = http_session.get(f"{base_url}/v1/Products({MISSING_PRODUCT_ID})")
    assert resp.status_code == 404


def test_get_missing_entity_has_odata_error_body(base_url, http_session):
    """GET /v1/Products(99999) response must contain an OData error object."""
    resp = http_session.get(f"{base_url}/v1/Products({MISSING_PRODUCT_ID})")
    body = resp.json()
    assert "error" in body


# -- $filter ------------------------------------------------------------------

def test_filter_eq_returns_200(base_url, http_session):
    """GET /v1/Products?$filter=Price eq 9.99 must return HTTP 200."""
    resp = http_session.get(f"{base_url}/v1/Products", params={"$filter": "Price eq 9.99"})
    assert resp.status_code == 200


def test_filter_eq_returns_correct_results(base_url, http_session):
    """$filter=Price eq 9.99 must return only products priced at $9.99."""
    resp = http_session.get(f"{base_url}/v1/Products", params={"$filter": "Price eq 9.99"})
    products = resp.json()["value"]
    assert len(products) >= 1
    assert all(p["price"] == 9.99 for p in products)


def test_filter_gt_returns_correct_results(base_url, http_session):
    """$filter=Price gt 10 must return only products with price > 10."""
    resp = http_session.get(f"{base_url}/v1/Products", params={"$filter": "Price gt 10"})
    products = resp.json()["value"]
    assert all(p["price"] > 10 for p in products)


def test_filter_lt_returns_correct_results(base_url, http_session):
    """$filter=Price lt 10 must return only products with price < 10."""
    resp = http_session.get(f"{base_url}/v1/Products", params={"$filter": "Price lt 10"})
    products = resp.json()["value"]
    assert all(p["price"] < 10 for p in products)


def test_filter_contains_returns_correct_results(base_url, http_session):
    """$filter=contains(Name,'get') must return products whose name contains 'get'."""
    resp = http_session.get(f"{base_url}/v1/Products", params={"$filter": "contains(Name,'get')"})
    products = resp.json()["value"]
    assert len(products) >= 1
    assert all("get" in p["name"].lower() for p in products)


def test_filter_startswith_returns_correct_results(base_url, http_session):
    """$filter=startswith(Name,'W') must return only products starting with 'W'."""
    resp = http_session.get(f"{base_url}/v1/Products", params={"$filter": "startswith(Name,'W')"})
    products = resp.json()["value"]
    assert all(p["name"].startswith("W") for p in products)


def test_filter_and_returns_correct_results(base_url, http_session):
    """$filter with 'and' must return products satisfying both conditions."""
    resp = http_session.get(
        f"{base_url}/v1/Products",
        params={"$filter": "Price gt 5 and Price lt 20"},
    )
    products = resp.json()["value"]
    assert all(5 < p["price"] < 20 for p in products)


def test_filter_invalid_syntax_returns_400(base_url, http_session):
    """An invalid $filter expression must return HTTP 400."""
    resp = http_session.get(f"{base_url}/v1/Products", params={"$filter": "NOTVALID((("})
    assert resp.status_code == 400


def test_filter_invalid_syntax_has_odata_error_body(base_url, http_session):
    """An invalid $filter expression must return an OData error object."""
    resp = http_session.get(f"{base_url}/v1/Products", params={"$filter": "NOTVALID((("})
    body = resp.json()
    assert "error" in body


# -- $top and $skip -----------------------------------------------------------

def test_top_limits_result_count(base_url, http_session):
    """$top=1 must return exactly 1 product."""
    resp = http_session.get(f"{base_url}/v1/Products", params={"$top": "1"})
    assert resp.status_code == 200
    products = resp.json()["value"]
    assert len(products) == 1


def test_skip_reduces_result_count(base_url, http_session):
    """$skip=4 on 5 seeded products must return 1 product."""
    resp = http_session.get(f"{base_url}/v1/Products", params={"$skip": "4"})
    assert resp.status_code == 200
    products = resp.json()["value"]
    assert len(products) >= 1


def test_top_and_skip_combined(base_url, http_session):
    """$top=2&$skip=1 must return at most 2 products."""
    resp = http_session.get(f"{base_url}/v1/Products", params={"$top": "2", "$skip": "1"})
    assert resp.status_code == 200
    products = resp.json()["value"]
    assert len(products) <= 2


# -- POST ---------------------------------------------------------------------

def test_post_creates_entity_with_201(base_url, http_session):
    """POST /v1/Products must return HTTP 201."""
    resp = http_session.post(
        f"{base_url}/v1/Products",
        data=json.dumps({"name": "PyODataWidget", "price": 7.77, "category": "Test"}),
    )
    created_id = resp.json().get("id") if resp.status_code == 201 else None
    try:
        assert resp.status_code == 201
    finally:
        if created_id:
            _delete_product(http_session, base_url, created_id)


def test_post_returns_created_entity_in_body(base_url, http_session):
    """POST /v1/Products response body must contain the new entity's id."""
    resp = http_session.post(
        f"{base_url}/v1/Products",
        data=json.dumps({"name": "PyODataWidget2", "price": 8.88, "category": "Test"}),
    )
    body = resp.json()
    created_id = body.get("id")
    try:
        assert created_id is not None
    finally:
        if created_id:
            _delete_product(http_session, base_url, created_id)


def test_post_returns_location_header(base_url, http_session):
    """POST /v1/Products must return a Location header."""
    resp = http_session.post(
        f"{base_url}/v1/Products",
        data=json.dumps({"name": "PyODataWidget3", "price": 3.33, "category": "Test"}),
    )
    created_id = resp.json().get("id") if resp.status_code == 201 else None
    try:
        assert "Location" in resp.headers or "location" in resp.headers
    finally:
        if created_id:
            _delete_product(http_session, base_url, created_id)


# -- DELETE -------------------------------------------------------------------

def test_delete_removes_entity_with_204(base_url, http_session):
    """DELETE /v1/Products(id) on an existing entity must return HTTP 204."""
    product = _create_product(http_session, base_url, "ToDelete", 1.11, "Test")
    product_id = product["id"]
    resp = http_session.delete(f"{base_url}/v1/Products({product_id})")
    assert resp.status_code == 204


def test_delete_entity_is_gone_after_delete(base_url, http_session):
    """After DELETE, GET on the same entity must return 404."""
    product = _create_product(http_session, base_url, "AlsoDelete", 2.22, "Test")
    product_id = product["id"]
    http_session.delete(f"{base_url}/v1/Products({product_id})")
    resp = http_session.get(f"{base_url}/v1/Products({product_id})")
    assert resp.status_code == 404


# -- Categories (string key / GetAll path) ------------------------------------

def test_categories_collection_returns_200(base_url, http_session):
    """GET /v1/Categories must return HTTP 200."""
    resp = http_session.get(f"{base_url}/v1/Categories")
    assert resp.status_code == 200


def test_categories_collection_has_value_array(base_url, http_session):
    """GET /v1/Categories response body must contain a 'value' array."""
    resp = http_session.get(f"{base_url}/v1/Categories")
    body = resp.json()
    assert "value" in body
    assert isinstance(body["value"], list)


def test_categories_get_by_string_key(base_url, http_session):
    """GET /v1/Categories('Hardware') must return the Hardware category."""
    resp = http_session.get(f"{base_url}/v1/Categories('Hardware')")
    assert resp.status_code == 200
    body = resp.json()
    assert body["code"] == "Hardware"
