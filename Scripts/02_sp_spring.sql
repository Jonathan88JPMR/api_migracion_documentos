/* ============================================================
   Stored procedures - api_migracion_documentos
   Base: HASS_SPRING_PRUEBA (en produccion ejecutar tambien
   en HP_SPRING_PRD)

   Resuelven la relacion obligacion (factura) -> OC/OS
   en dbo.AP_Documentos y el conteo de GD_Archivo de pruebas.
   Convencion: entrada @json NVARCHAR(MAX), salida FOR JSON.
   ============================================================ */
GO

CREATE OR ALTER PROCEDURE dbo.sp_mig_respuesta
    @status NVARCHAR(20),
    @message NVARCHAR(500),
    @data NVARCHAR(MAX) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SELECT @status AS [status], @message AS [message], @data AS [data]
    FOR JSON PATH, WITHOUT_ARRAY_WRAPPER;
END;
GO

/* ----------------------------------------------------------
   Referencias OC/OS de un conjunto de obligaciones.
   @json: { "claves": [ {"p":"1525","t":"FA","n":"F007-0002740"} ] }
   Devuelve [{p,t,n,refTipo,refNum}] (una fila por referencia).
   ---------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_mig_referencias
    @json NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @__data NVARCHAR(MAX);
    DECLARE @err NVARCHAR(500);
    BEGIN TRY
        SET @__data = ISNULL((
            SELECT CAST(d.Proveedor AS VARCHAR(20)) AS p,
                   RTRIM(d.ObligacionTipoDocumento) AS t,
                   RTRIM(d.ObligacionNumeroDocumento) AS n,
                   RTRIM(d.ReferenciaTipoDocumento) AS refTipo,
                   RTRIM(d.ReferenciaNumeroDocumento) AS refNum,
                   d.RutaArchivo AS ruta
            FROM dbo.AP_Documentos d
            WHERE d.ReferenciaTipoDocumento IS NOT NULL
              AND d.ReferenciaNumeroDocumento IS NOT NULL
              AND EXISTS (
                    SELECT 1 FROM OPENJSON(@json, '$.claves')
                    WITH (p VARCHAR(20) '$.p', t VARCHAR(5) '$.t', n VARCHAR(30) '$.n') c
                    WHERE CAST(d.Proveedor AS VARCHAR(20)) = c.p
                      AND RTRIM(d.ObligacionTipoDocumento) = c.t
                      AND RTRIM(d.ObligacionNumeroDocumento) = c.n)
            FOR JSON PATH), '[]');
        EXEC dbo.sp_mig_respuesta 'success', 'Referencias', @__data;
    END TRY
    BEGIN CATCH
        SET @err = ERROR_MESSAGE();
        EXEC dbo.sp_mig_respuesta 'error', @err, NULL;
    END CATCH
END;
GO

/* ----------------------------------------------------------
   Subconjunto de claves cuya obligacion tiene al menos una
   referencia del tipo indicado (filtro OC/OS del listado).
   @json: { "refTipo": "OC"|"SO",
            "claves": [ {"p":..,"t":..,"n":..} ] }
   Devuelve [{p,t,n}].
   ---------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_mig_claves_por_ref
    @json NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @__data NVARCHAR(MAX);
    DECLARE @err NVARCHAR(500);
    BEGIN TRY
        DECLARE @refTipo VARCHAR(5) = JSON_VALUE(@json, '$.refTipo');

        SET @__data = ISNULL((
            SELECT DISTINCT CAST(d.Proveedor AS VARCHAR(20)) AS p,
                   RTRIM(d.ObligacionTipoDocumento) AS t,
                   RTRIM(d.ObligacionNumeroDocumento) AS n
            FROM dbo.AP_Documentos d
            WHERE RTRIM(d.ReferenciaTipoDocumento) = @refTipo
              AND EXISTS (
                    SELECT 1 FROM OPENJSON(@json, '$.claves')
                    WITH (p VARCHAR(20) '$.p', t VARCHAR(5) '$.t', n VARCHAR(30) '$.n') c
                    WHERE CAST(d.Proveedor AS VARCHAR(20)) = c.p
                      AND RTRIM(d.ObligacionTipoDocumento) = c.t
                      AND RTRIM(d.ObligacionNumeroDocumento) = c.n)
            FOR JSON PATH), '[]');
        EXEC dbo.sp_mig_respuesta 'success', 'Claves por referencia', @__data;
    END TRY
    BEGIN CATCH
        SET @err = ERROR_MESSAGE();
        EXEC dbo.sp_mig_respuesta 'error', @err, NULL;
    END CATCH
END;
GO

/* ----------------------------------------------------------
   Obligaciones relacionadas a un numero de OC/OS
   (ejecucion personalizada por orden).
   @json: { "numero": "0000001710", "tipo": "OC"|"SO"|null }
   Devuelve [{p,t,n}].
   ---------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_mig_obligaciones_referencia
    @json NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @__data NVARCHAR(MAX);
    DECLARE @err NVARCHAR(500);
    BEGIN TRY
        DECLARE @numero VARCHAR(30) = JSON_VALUE(@json, '$.numero');
        DECLARE @tipo VARCHAR(5) = NULLIF(JSON_VALUE(@json, '$.tipo'), '');

        SET @__data = ISNULL((
            SELECT DISTINCT CAST(d.Proveedor AS VARCHAR(20)) AS p,
                   RTRIM(d.ObligacionTipoDocumento) AS t,
                   RTRIM(d.ObligacionNumeroDocumento) AS n
            FROM dbo.AP_Documentos d
            WHERE RTRIM(d.ReferenciaNumeroDocumento) = @numero
              AND (@tipo IS NULL OR RTRIM(d.ReferenciaTipoDocumento) = @tipo)
            FOR JSON PATH), '[]');
        EXEC dbo.sp_mig_respuesta 'success', 'Obligaciones de referencia', @__data;
    END TRY
    BEGIN CATCH
        SET @err = ERROR_MESSAGE();
        EXEC dbo.sp_mig_respuesta 'error', @err, NULL;
    END CATCH
END;
GO

/* ----------------------------------------------------------
   Conteo de GD_Archivo con ruta de PRUEBAS (stats dashboard).
   ---------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_mig_gd_prueba_count
    @json NVARCHAR(MAX) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @__data NVARCHAR(MAX);
    DECLARE @err NVARCHAR(500);
    BEGIN TRY
        SET @__data = (
            SELECT COUNT(*) AS total
            FROM dbo.GD_Archivo
            WHERE RutaArchivo LIKE '%SpringGestionDoc\PRUEBAS%'
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);
        EXEC dbo.sp_mig_respuesta 'success', 'GD pruebas', @__data;
    END TRY
    BEGIN CATCH
        SET @err = ERROR_MESSAGE();
        EXEC dbo.sp_mig_respuesta 'error', @err, NULL;
    END CATCH
END;
GO

/* ----------------------------------------------------------
   Permisos de ejecucion para el usuario del servicio.
   ---------------------------------------------------------- */
IF DATABASE_PRINCIPAL_ID('usr_migracion_adjuntos') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.sp_mig_respuesta               TO usr_migracion_adjuntos;
    GRANT EXECUTE ON dbo.sp_mig_referencias             TO usr_migracion_adjuntos;
    GRANT EXECUTE ON dbo.sp_mig_claves_por_ref          TO usr_migracion_adjuntos;
    GRANT EXECUTE ON dbo.sp_mig_obligaciones_referencia TO usr_migracion_adjuntos;
    GRANT EXECUTE ON dbo.sp_mig_gd_prueba_count         TO usr_migracion_adjuntos;
END;
GO
