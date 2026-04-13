package com.engen.ohdata.olingo;

import org.apache.olingo.client.api.ODataClient;
import org.apache.olingo.client.api.communication.request.cud.ODataDeleteRequest;
import org.apache.olingo.client.api.communication.request.cud.ODataEntityCreateRequest;
import org.apache.olingo.client.api.communication.request.retrieve.ODataEntityRequest;
import org.apache.olingo.client.api.communication.request.retrieve.ODataEntitySetRequest;
import org.apache.olingo.client.api.communication.request.retrieve.ODataMetadataRequest;
import org.apache.olingo.client.api.communication.response.ODataDeleteResponse;
import org.apache.olingo.client.api.communication.response.ODataEntityCreateResponse;
import org.apache.olingo.client.api.communication.response.ODataRetrieveResponse;
import org.apache.olingo.client.api.domain.ClientCollectionValue;
import org.apache.olingo.client.api.domain.ClientEntity;
import org.apache.olingo.client.api.domain.ClientEntitySet;
import org.apache.olingo.client.api.domain.ClientObjectFactory;
import org.apache.olingo.client.api.domain.ClientProperty;
import org.apache.olingo.client.api.domain.ClientValue;
import org.apache.olingo.client.core.ODataClientFactory;
import org.apache.olingo.commons.api.edm.Edm;
import org.apache.olingo.commons.api.edm.FullQualifiedName;
import org.apache.olingo.commons.api.format.ContentType;

import org.junit.jupiter.api.AfterAll;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.DisplayName;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.TestInstance;

import java.net.URI;
import java.util.ArrayList;
import java.util.List;

import static org.junit.jupiter.api.Assertions.*;

/**
 * Apache Olingo OData client integration tests against the OhData TestBench server.
 *
 * The server exposes two versioned roots:
 *   /v1 -- Products + Categories
 *   /v2 -- Products + Orders + Categories
 *
 * These tests target the v1 root and use the Products entity set, which supports
 * full query options ($filter, $top, $skip, $orderby) and CRUD operations.
 *
 * Base URL is read from the system property ODATA_BASE_URL, defaulting to
 * http://localhost:8080 when running outside Docker.
 */
@TestInstance(TestInstance.Lifecycle.PER_CLASS)
@DisplayName("OhData -- Apache Olingo OData client integration tests")
public class OlingoClientIntegrationTest {

    private static final String DEFAULT_BASE_URL = "http://localhost:8080";

    /** The v1 OData service root -- e.g. http://testbench:8080/v1 */
    private String serviceRoot;

    /** Shared Olingo client instance -- thread-safe, reused across all tests */
    private ODataClient client;

    /**
     * IDs of entities created during the test run so they can be cleaned up
     * in @AfterAll even if individual tests fail.
     */
    private final List<Integer> createdProductIds = new ArrayList<>();

    @BeforeAll
    void setUp() {
        String baseUrl = System.getProperty("ODATA_BASE_URL", DEFAULT_BASE_URL);
        // Strip trailing slash if present
        if (baseUrl.endsWith("/")) {
            baseUrl = baseUrl.substring(0, baseUrl.length() - 1);
        }
        serviceRoot = baseUrl + "/v1";

        client = ODataClientFactory.getClient();
        // Use JSON (minimal metadata) as the preferred wire format
        client.getConfiguration().setDefaultPubFormat(ContentType.JSON);
    }

    @AfterAll
    void tearDown() {
        // Best-effort cleanup -- delete any products created during the test run
        for (Integer id : createdProductIds) {
            try {
                URI deleteUri = client.newURIBuilder(serviceRoot)
                        .appendEntitySetSegment("Products")
                        .appendKeySegment(id)
                        .build();
                client.getCUDRequestFactory().getDeleteRequest(deleteUri).execute();
            } catch (Exception ignored) {
                // Ignore errors during cleanup -- the server may have already removed the entity
            }
        }
    }

    // ── $metadata ────────────────────────────────────────────────────────────────

    @Test
    @DisplayName("$metadata document is parseable by the Olingo EDM parser")
    void metadataIsParseable() {
        ODataMetadataRequest metaRequest = client.getRetrieveRequestFactory()
                .getMetadataRequest(serviceRoot);

        ODataRetrieveResponse<Edm> response = metaRequest.execute();

        assertEquals(200, response.getStatusCode(),
                "$metadata should return HTTP 200");

        Edm edm = response.getBody();
        assertNotNull(edm, "EDM model must not be null");

        // The OhData TestBench registers Products, Categories in v1.
        // Olingo exposes entity types by fully-qualified name under the namespace
        // the server emits in the CSDL. We verify at least one schema is present
        // and that the entity container exists.
        assertFalse(edm.getSchemas().isEmpty(),
                "EDM must contain at least one schema");
        assertNotNull(edm.getEntityContainer(),
                "EDM must have a default entity container");
    }

    // ── GET collection ───────────────────────────────────────────────────────────

    @Test
    @DisplayName("GET Products collection returns 200 with a non-empty entity list")
    void getCollectionReturns200WithEntities() {
        URI uri = client.newURIBuilder(serviceRoot)
                .appendEntitySetSegment("Products")
                .build();

        ODataEntitySetRequest<ClientEntitySet> request =
                client.getRetrieveRequestFactory().getEntitySetRequest(uri);

        ODataRetrieveResponse<ClientEntitySet> response = request.execute();

        assertEquals(200, response.getStatusCode(),
                "GET /Products should return 200");

        ClientEntitySet entitySet = response.getBody();
        assertNotNull(entitySet, "Response body must not be null");
        assertFalse(entitySet.getEntities().isEmpty(),
                "Products collection must have at least one entity (seeded data)");
    }

    // ── GET by key ───────────────────────────────────────────────────────────────

    @Test
    @DisplayName("GET Products(1) returns 200 with the correct entity")
    void getByKeyReturnsCorrectEntity() {
        URI uri = client.newURIBuilder(serviceRoot)
                .appendEntitySetSegment("Products")
                .appendKeySegment(1)
                .build();

        ODataEntityRequest<ClientEntity> request =
                client.getRetrieveRequestFactory().getEntityRequest(uri);

        ODataRetrieveResponse<ClientEntity> response = request.execute();

        assertEquals(200, response.getStatusCode(),
                "GET /Products(1) should return 200");

        ClientEntity entity = response.getBody();
        assertNotNull(entity, "Entity must not be null");

        // Seeded data: id=1 is Widget at $9.99 in Hardware
        ClientProperty nameProp = entity.getProperty("Name");
        assertNotNull(nameProp, "Entity must have a Name property");
        assertEquals("Widget", nameProp.getValue().toString(),
                "Product 1 must be named Widget");
    }

    // ── $filter ──────────────────────────────────────────────────────────────────

    @Test
    @DisplayName("$filter=Category eq 'Hardware' returns only Hardware products")
    void filterByCategory() {
        URI uri = client.newURIBuilder(serviceRoot)
                .appendEntitySetSegment("Products")
                .filter("Category eq 'Hardware'")
                .build();

        ODataEntitySetRequest<ClientEntitySet> request =
                client.getRetrieveRequestFactory().getEntitySetRequest(uri);

        ODataRetrieveResponse<ClientEntitySet> response = request.execute();

        assertEquals(200, response.getStatusCode(),
                "$filter request should return 200");

        List<ClientEntity> entities = response.getBody().getEntities();
        assertFalse(entities.isEmpty(),
                "Filtered result must contain at least one Hardware product");

        for (ClientEntity entity : entities) {
            ClientProperty cat = entity.getProperty("Category");
            assertNotNull(cat, "Each entity must have a Category property");
            assertEquals("Hardware", cat.getValue().toString(),
                    "All filtered entities must be in the Hardware category");
        }
    }

    @Test
    @DisplayName("$filter=Price gt 20 returns only products priced above 20")
    void filterByPrice() {
        URI uri = client.newURIBuilder(serviceRoot)
                .appendEntitySetSegment("Products")
                .filter("Price gt 20")
                .build();

        ODataEntitySetRequest<ClientEntitySet> request =
                client.getRetrieveRequestFactory().getEntitySetRequest(uri);

        ODataRetrieveResponse<ClientEntitySet> response = request.execute();

        assertEquals(200, response.getStatusCode(),
                "$filter by price should return 200");

        List<ClientEntity> entities = response.getBody().getEntities();
        assertFalse(entities.isEmpty(),
                "There should be at least one product priced above 20 (Gadget=$24.99, Thingamajig=$39.99)");

        for (ClientEntity entity : entities) {
            ClientProperty priceProp = entity.getProperty("Price");
            assertNotNull(priceProp, "Each entity must have a Price property");
            double price = Double.parseDouble(priceProp.getValue().toString());
            assertTrue(price > 20.0,
                    "All filtered entities must have Price > 20, got: " + price);
        }
    }

    // ── $top / $skip ─────────────────────────────────────────────────────────────

    @Test
    @DisplayName("$top=2 returns exactly 2 entities")
    void topTwoEntities() {
        URI uri = client.newURIBuilder(serviceRoot)
                .appendEntitySetSegment("Products")
                .top(2)
                .build();

        ODataEntitySetRequest<ClientEntitySet> request =
                client.getRetrieveRequestFactory().getEntitySetRequest(uri);

        ODataRetrieveResponse<ClientEntitySet> response = request.execute();

        assertEquals(200, response.getStatusCode(),
                "$top=2 should return 200");

        List<ClientEntity> entities = response.getBody().getEntities();
        assertEquals(2, entities.size(),
                "$top=2 must return exactly 2 entities");
    }

    @Test
    @DisplayName("$skip=3 returns fewer entities than the full collection")
    void skipThreeEntities() {
        // First, get the total count without $skip
        URI allUri = client.newURIBuilder(serviceRoot)
                .appendEntitySetSegment("Products")
                .build();
        int totalCount = client.getRetrieveRequestFactory()
                .getEntitySetRequest(allUri)
                .execute()
                .getBody()
                .getEntities()
                .size();

        URI uri = client.newURIBuilder(serviceRoot)
                .appendEntitySetSegment("Products")
                .skip(3)
                .build();

        ODataEntitySetRequest<ClientEntitySet> request =
                client.getRetrieveRequestFactory().getEntitySetRequest(uri);

        ODataRetrieveResponse<ClientEntitySet> response = request.execute();

        assertEquals(200, response.getStatusCode(),
                "$skip=3 should return 200");

        int skippedCount = response.getBody().getEntities().size();
        assertTrue(skippedCount < totalCount,
                "$skip=3 must return fewer entities than the full collection (" +
                        skippedCount + " vs " + totalCount + ")");
    }

    @Test
    @DisplayName("$top=2 and $skip=1 returns a page of at most 2 entities")
    void topAndSkipPagination() {
        URI uri = client.newURIBuilder(serviceRoot)
                .appendEntitySetSegment("Products")
                .top(2)
                .skip(1)
                .build();

        ODataEntitySetRequest<ClientEntitySet> request =
                client.getRetrieveRequestFactory().getEntitySetRequest(uri);

        ODataRetrieveResponse<ClientEntitySet> response = request.execute();

        assertEquals(200, response.getStatusCode(),
                "$top=2&$skip=1 should return 200");

        int count = response.getBody().getEntities().size();
        assertTrue(count <= 2,
                "$top=2 must not return more than 2 entities, got: " + count);
    }

    // ── POST (create) ─────────────────────────────────────────────────────────────

    @Test
    @DisplayName("POST to Products creates a new entity and returns 201")
    void postCreatesNewEntity() {
        ClientObjectFactory factory = client.getObjectFactory();

        ClientEntity newProduct = factory.newEntity(
                new FullQualifiedName("OhData.TestBench.AspNetCore", "Product"));

        newProduct.getProperties().add(
                factory.newPrimitiveProperty("Name",
                        factory.newPrimitiveValueBuilder().buildString("OlingoTestProduct")));
        newProduct.getProperties().add(
                factory.newPrimitiveProperty("Price",
                        factory.newPrimitiveValueBuilder().buildDouble(7.77)));
        newProduct.getProperties().add(
                factory.newPrimitiveProperty("Category",
                        factory.newPrimitiveValueBuilder().buildString("Test")));

        URI collectionUri = client.newURIBuilder(serviceRoot)
                .appendEntitySetSegment("Products")
                .build();

        ODataEntityCreateRequest<ClientEntity> createRequest =
                client.getCUDRequestFactory().getEntityCreateRequest(collectionUri, newProduct);

        ODataEntityCreateResponse<ClientEntity> createResponse = createRequest.execute();

        // OhData returns 201 Created for POST
        assertEquals(201, createResponse.getStatusCode(),
                "POST should return 201 Created");

        ClientEntity created = createResponse.getBody();
        assertNotNull(created, "Response body must contain the created entity");

        ClientProperty idProp = created.getProperty("Id");
        assertNotNull(idProp, "Created entity must have an Id property");

        // Track the created ID for cleanup in @AfterAll
        int createdId = Integer.parseInt(idProp.getValue().toString());
        createdProductIds.add(createdId);

        ClientProperty nameProp = created.getProperty("Name");
        assertNotNull(nameProp, "Created entity must have a Name property");
        assertEquals("OlingoTestProduct", nameProp.getValue().toString(),
                "Created entity name must match the submitted value");
    }

    // ── DELETE ───────────────────────────────────────────────────────────────────

    @Test
    @DisplayName("DELETE removes an entity and returns 204")
    void deleteRemovesEntity() {
        // First create a product to delete -- so we don't touch seeded data
        ClientObjectFactory factory = client.getObjectFactory();

        ClientEntity newProduct = factory.newEntity(
                new FullQualifiedName("OhData.TestBench.AspNetCore", "Product"));
        newProduct.getProperties().add(
                factory.newPrimitiveProperty("Name",
                        factory.newPrimitiveValueBuilder().buildString("DeleteMe-Olingo")));
        newProduct.getProperties().add(
                factory.newPrimitiveProperty("Price",
                        factory.newPrimitiveValueBuilder().buildDouble(0.01)));
        newProduct.getProperties().add(
                factory.newPrimitiveProperty("Category",
                        factory.newPrimitiveValueBuilder().buildString("Test")));

        URI collectionUri = client.newURIBuilder(serviceRoot)
                .appendEntitySetSegment("Products")
                .build();

        ODataEntityCreateResponse<ClientEntity> createResponse =
                client.getCUDRequestFactory()
                        .getEntityCreateRequest(collectionUri, newProduct)
                        .execute();

        assertEquals(201, createResponse.getStatusCode(),
                "Setup POST should return 201 before the DELETE test");

        int newId = Integer.parseInt(
                createResponse.getBody().getProperty("Id").getValue().toString());

        // Now delete it
        URI deleteUri = client.newURIBuilder(serviceRoot)
                .appendEntitySetSegment("Products")
                .appendKeySegment(newId)
                .build();

        ODataDeleteRequest deleteRequest =
                client.getCUDRequestFactory().getDeleteRequest(deleteUri);
        ODataDeleteResponse deleteResponse = deleteRequest.execute();

        assertEquals(204, deleteResponse.getStatusCode(),
                "DELETE should return 204 No Content");

        // Verify it is gone -- OhData returns 404 for a missing entity
        URI getUri = client.newURIBuilder(serviceRoot)
                .appendEntitySetSegment("Products")
                .appendKeySegment(newId)
                .build();

        try {
            ODataRetrieveResponse<ClientEntity> getResponse =
                    client.getRetrieveRequestFactory()
                            .getEntityRequest(getUri)
                            .execute();
            // If we reach here the server did not return 404 -- fail the test
            fail("Expected a 404 after DELETE, but got: " + getResponse.getStatusCode());
        } catch (Exception ex) {
            // Olingo throws when the server returns 4xx -- this is expected
            assertTrue(ex.getMessage() != null,
                    "Exception from 404 must carry a message");
        }
    }
}
