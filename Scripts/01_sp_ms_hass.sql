/* ============================================================
   Stored procedures - api_migracion_documentos
   Base: MS_HASS_PROVEEDOR (portal de proveedores)

   Convencion (igual que api_alquiler_maquinaria):
   - Entrada: parametro @json NVARCHAR(MAX) con JSON_VALUE/OPENJSON.
   - Salida: FOR JSON PATH, envuelta por sp_mig_respuesta
     ({status, message, data}) salvo listados que devuelven
     el JSON de datos directo.
   - Previews de binarios devuelven rowset (varbinary no cabe en JSON).
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
   Entornos: lista + entorno activo
   ---------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_mig_entornos
    @json NVARCHAR(MAX) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @__data NVARCHAR(MAX);
    DECLARE @err NVARCHAR(500);
    BEGIN TRY
        DECLARE @activo VARCHAR(20) = (SELECT TOP 1 Entorno FROM dbo.Config_Activo);
        IF @activo IS NULL SET @activo = 'PRUEBA';
        SET @__data = (
            SELECT
                @activo AS activo,
                JSON_QUERY((SELECT Entorno, Descripcion, CAST(ISNULL(ProduccionHabilitado, 0) AS BIT) AS ProduccionHabilitado
                 FROM dbo.Entornos_Migracion
                 WHERE Activo = 1
                 ORDER BY Entorno
                 FOR JSON PATH)) AS entornos
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);
        EXEC dbo.sp_mig_respuesta 'success', 'Entornos', @__data;
    END TRY
    BEGIN CATCH
        SET @err = ERROR_MESSAGE();
        EXEC dbo.sp_mig_respuesta 'error', @err, NULL;
    END CATCH
END;
GO

/* ----------------------------------------------------------
   Cambiar entorno activo.
   @json: { "accion": "G"|"S", "entorno": "PRUEBA",
            "usuario": "dashboard", "ip": "x.x.x.x" }
   En "S" valida existencia y ProduccionHabilitado, hace MERGE
   en Config_Activo y audita en Auditoria_Cambio_Entorno.
   ---------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_mig_entorno_activo
    @json NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @__data NVARCHAR(MAX);
    DECLARE @err NVARCHAR(500);
    BEGIN TRY
        DECLARE @accion NVARCHAR(1) = JSON_VALUE(@json, '$.accion');

        IF @accion = 'G'
        BEGIN
            DECLARE @actual VARCHAR(20) = (SELECT TOP 1 Entorno FROM dbo.Config_Activo);
            IF @actual IS NULL SET @actual = 'PRUEBA';
            SET @__data = (SELECT @actual AS activo FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);
            EXEC dbo.sp_mig_respuesta 'success', 'Entorno activo', @__data;
            RETURN;
        END

        IF @accion = 'S'
        BEGIN
            DECLARE @nuevo VARCHAR(20) = JSON_VALUE(@json, '$.entorno');
            DECLARE @usuario VARCHAR(100) = ISNULL(JSON_VALUE(@json, '$.usuario'), 'dashboard');
            DECLARE @ip VARCHAR(50) = ISNULL(JSON_VALUE(@json, '$.ip'), '');

            IF NOT EXISTS (SELECT 1 FROM dbo.Entornos_Migracion WHERE Entorno = @nuevo AND Activo = 1)
            BEGIN
                EXEC dbo.sp_mig_respuesta 'error', 'Entorno no encontrado', NULL;
                RETURN;
            END

            IF EXISTS (SELECT 1 FROM dbo.Entornos_Migracion
                       WHERE Entorno = @nuevo
                         AND UPPER(MODO_SERVICIO) = 'PRODUCCION'
                         AND ISNULL(ProduccionHabilitado, 0) = 0)
            BEGIN
                EXEC dbo.sp_mig_respuesta 'error', 'Produccion no habilitada', NULL;
                RETURN;
            END

            DECLARE @anterior VARCHAR(20) = (SELECT TOP 1 Entorno FROM dbo.Config_Activo);
            IF @anterior IS NULL SET @anterior = 'PRUEBA';

            MERGE dbo.Config_Activo AS t
            USING (VALUES (1, @nuevo)) AS s(Id, Entorno)
            ON t.Id = s.Id
            WHEN MATCHED THEN
                UPDATE SET Entorno = s.Entorno, FechaCambio = GETDATE()
            WHEN NOT MATCHED THEN
                INSERT (Id, Entorno) VALUES (s.Id, s.Entorno);

            INSERT INTO dbo.Auditoria_Cambio_Entorno (EntornoAnterior, EntornoNuevo, Usuario, Fecha, IP)
            VALUES (@anterior, @nuevo, @usuario, GETDATE(), @ip);

            SET @__data = (SELECT @nuevo AS entorno_activo, @anterior AS entorno_anterior
                           FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);
            EXEC dbo.sp_mig_respuesta 'success', 'Entorno actualizado', @__data;
            RETURN;
        END

        EXEC dbo.sp_mig_respuesta 'error', 'Accion no soportada', NULL;
    END TRY
    BEGIN CATCH
        SET @err = ERROR_MESSAGE();
        EXEC dbo.sp_mig_respuesta 'error', @err, NULL;
    END CATCH
END;
GO

/* ----------------------------------------------------------
   Configuracion de un entorno (tabla Entornos_Migracion).
   @json: { "accion": "G"|"U", "entorno": "PRUEBA", "data": { ... } }
   En "U" solo se actualizan las columnas configurables.
   ---------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_mig_config
    @json NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @__data NVARCHAR(MAX);
    DECLARE @err NVARCHAR(500);
    BEGIN TRY
        DECLARE @accion NVARCHAR(1) = JSON_VALUE(@json, '$.accion');
        DECLARE @entorno VARCHAR(20) = JSON_VALUE(@json, '$.entorno');

        IF @accion = 'G'
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM dbo.Entornos_Migracion WHERE Entorno = @entorno)
            BEGIN
                EXEC dbo.sp_mig_respuesta 'error', 'Entorno no encontrado', NULL;
                RETURN;
            END
            -- FECHA_* se agregan en preparar_paso_produccion.sql; tolerar su
            -- ausencia para no romper la config si el SP corre antes.
            DECLARE @fmin VARCHAR(10) = NULL, @fmax VARCHAR(10) = NULL;
            IF COL_LENGTH('dbo.Entornos_Migracion', 'FECHA_MINIMA') IS NOT NULL
                SELECT @fmin = FECHA_MINIMA, @fmax = FECHA_MAXIMA
                FROM dbo.Entornos_Migracion WHERE Entorno = @entorno;
            SET @__data = (
                SELECT Entorno, Descripcion, MODO_SERVICIO, SERVIDOR_MS_HASS, BD_MS_HASS,
                       SERVIDOR_SPRING, BD_SPRING, RUTA_PRUEBA_LOCAL, RUTA_RAIZ,
                       TIPO_COPIA_PRUEBA, MODO_PRUEBA, BATCH_PRUEBA, BATCH_AP_DOCUMENTO_PRUEBA,
                       TABLA_ADJUNTOS, USUARIO_MIGRACION, SQL_USER,
                       @fmin AS FECHA_MINIMA, @fmax AS FECHA_MAXIMA,
                       CAST(ISNULL(ProduccionHabilitado, 0) AS BIT) AS ProduccionHabilitado,
                       CAST(ISNULL(Activo, 0) AS BIT) AS Activo
                FROM dbo.Entornos_Migracion
                WHERE Entorno = @entorno
                FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);
            EXEC dbo.sp_mig_respuesta 'success', 'Configuracion', @__data;
            RETURN;
        END

        IF @accion = 'U'
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM dbo.Entornos_Migracion WHERE Entorno = @entorno)
            BEGIN
                EXEC dbo.sp_mig_respuesta 'error', 'Entorno no encontrado', NULL;
                RETURN;
            END
            UPDATE dbo.Entornos_Migracion
            SET MODO_SERVICIO            = ISNULL(JSON_VALUE(@json, '$.data.MODO_SERVICIO'), MODO_SERVICIO),
                SERVIDOR_MS_HASS         = ISNULL(JSON_VALUE(@json, '$.data.SERVIDOR_MS_HASS'), SERVIDOR_MS_HASS),
                BD_MS_HASS               = ISNULL(JSON_VALUE(@json, '$.data.BD_MS_HASS'), BD_MS_HASS),
                SERVIDOR_SPRING          = ISNULL(JSON_VALUE(@json, '$.data.SERVIDOR_SPRING'), SERVIDOR_SPRING),
                BD_SPRING                = ISNULL(JSON_VALUE(@json, '$.data.BD_SPRING'), BD_SPRING),
                RUTA_PRUEBA_LOCAL        = ISNULL(JSON_VALUE(@json, '$.data.RUTA_PRUEBA_LOCAL'), RUTA_PRUEBA_LOCAL),
                RUTA_RAIZ                = ISNULL(JSON_VALUE(@json, '$.data.RUTA_RAIZ'), RUTA_RAIZ),
                TIPO_COPIA_PRUEBA        = ISNULL(JSON_VALUE(@json, '$.data.TIPO_COPIA_PRUEBA'), TIPO_COPIA_PRUEBA),
                MODO_PRUEBA              = ISNULL(JSON_VALUE(@json, '$.data.MODO_PRUEBA'), MODO_PRUEBA),
                BATCH_PRUEBA             = ISNULL(JSON_VALUE(@json, '$.data.BATCH_PRUEBA'), BATCH_PRUEBA),
                BATCH_AP_DOCUMENTO_PRUEBA = ISNULL(JSON_VALUE(@json, '$.data.BATCH_AP_DOCUMENTO_PRUEBA'), BATCH_AP_DOCUMENTO_PRUEBA),
                TABLA_ADJUNTOS           = ISNULL(JSON_VALUE(@json, '$.data.TABLA_ADJUNTOS'), TABLA_ADJUNTOS),
                USUARIO_MIGRACION        = ISNULL(JSON_VALUE(@json, '$.data.USUARIO_MIGRACION'), USUARIO_MIGRACION),
                SQL_USER                 = ISNULL(JSON_VALUE(@json, '$.data.SQL_USER'), SQL_USER),
                ProduccionHabilitado     = ISNULL(TRY_CAST(JSON_VALUE(@json, '$.data.ProduccionHabilitado') AS BIT), ProduccionHabilitado)
            WHERE Entorno = @entorno;

            IF COL_LENGTH('dbo.Entornos_Migracion', 'FECHA_MINIMA') IS NOT NULL
                UPDATE dbo.Entornos_Migracion
                SET FECHA_MINIMA = ISNULL(JSON_VALUE(@json, '$.data.FECHA_MINIMA'), FECHA_MINIMA),
                    FECHA_MAXIMA = ISNULL(JSON_VALUE(@json, '$.data.FECHA_MAXIMA'), FECHA_MAXIMA)
                WHERE Entorno = @entorno;

            EXEC dbo.sp_mig_respuesta 'success', 'Configuracion actualizada', NULL;
            RETURN;
        END

        EXEC dbo.sp_mig_respuesta 'error', 'Accion no soportada', NULL;
    END TRY
    BEGIN CATCH
        SET @err = ERROR_MESSAGE();
        EXEC dbo.sp_mig_respuesta 'error', @err, NULL;
    END CATCH
END;
GO

/* ----------------------------------------------------------
   Estadisticas del dashboard (solo lado portal; el conteo de
   GD_Archivo PRUEBAS lo aporta sp_mig_gd_prueba_count en Spring).
   ---------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_mig_stats
    @json NVARCHAR(MAX) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @__data NVARCHAR(MAX);
    DECLARE @err NVARCHAR(500);
    BEGIN TRY
        SET @__data = (
            SELECT
                (SELECT COUNT(*) FROM dbo.MS_AP_DocumentoArchivo) AS ap_total,
                (SELECT COUNT(*) FROM dbo.MS_AP_DocumentoArchivo WHERE Gadatos IS NOT NULL) AS ap_with_data,
                (SELECT COUNT(*) FROM dbo.MS_AP_DocumentoArchivo a
                 LEFT JOIN dbo.MS_AP_DocumentoArchivoTraslado t ON t.IdApDocumentoArchivo = a.IdApDocumentoArchivo
                 WHERE a.Gadatos IS NOT NULL AND (t.IdApDocumentoArchivo IS NULL OR t.Estado IN ('PENDIENTE', 'ERROR'))) AS ap_pending,
                (SELECT COUNT(*) FROM dbo.MS_AP_DocumentoArchivoTraslado WHERE Estado = 'COMPLETADO') AS ap_done,
                JSON_QUERY((SELECT a.Gatipo AS Gatipo, COUNT(*) AS total,
                        ISNULL(SUM(CASE WHEN t.Estado = 'COMPLETADO' THEN 1 ELSE 0 END), 0) AS completados
                 FROM dbo.MS_AP_DocumentoArchivo a
                 LEFT JOIN dbo.MS_AP_DocumentoArchivoTraslado t ON t.IdApDocumentoArchivo = a.IdApDocumentoArchivo
                 GROUP BY a.Gatipo
                 ORDER BY COUNT(*) DESC
                 FOR JSON PATH)) AS ap_by_gatipo,
                JSON_QUERY((SELECT Estado, COUNT(*) AS total FROM dbo.MS_AP_DocumentoArchivoTraslado
                 GROUP BY Estado ORDER BY COUNT(*) DESC
                 FOR JSON PATH)) AS ap_by_estado,
                (SELECT COUNT(*) FROM dbo.MS_Archivo) AS ms_total,
                (SELECT COUNT(*) FROM dbo.MS_Archivo a
                 LEFT JOIN dbo.MS_ArchivoTraslado t ON t.IdArchivo = a.IdArchivo
                 WHERE t.IdArchivo IS NULL OR t.Estado IN ('PENDIENTE', 'ERROR')) AS ms_pending,
                (SELECT COUNT(*) FROM dbo.MS_ArchivoTraslado WHERE Estado = 'COMPLETADO') AS ms_done,
                JSON_QUERY((SELECT ISNULL(t.Estado, 'SIN_CONTROL') AS Estado, COUNT(*) AS total
                 FROM dbo.MS_Archivo a
                 LEFT JOIN dbo.MS_ArchivoTraslado t ON t.IdArchivo = a.IdArchivo
                 GROUP BY t.Estado
                 FOR JSON PATH)) AS ms_by_estado,
                (SELECT MAX(f) FROM (SELECT MAX(FechaTraslado) AS f FROM dbo.MS_AP_DocumentoArchivoTraslado
                                     UNION ALL SELECT MAX(FechaTraslado) FROM dbo.MS_ArchivoTraslado) u) AS last_run
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);
        EXEC dbo.sp_mig_respuesta 'success', 'Stats', @__data;
    END TRY
    BEGIN CATCH
        SET @err = ERROR_MESSAGE();
        EXEC dbo.sp_mig_respuesta 'error', @err, NULL;
    END CATCH
END;
GO

/* ----------------------------------------------------------
   Candidatos AP para el filtro OC/OS: ids + claves de
   obligacion (proveedor|tipo|numero) sin paginar.
   @json: { "estado": "COMPLETADO"|null, "gatipo": "..."|null }
   ---------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_mig_candidatos_ap
    @json NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @__data NVARCHAR(MAX);
    DECLARE @err NVARCHAR(500);
    BEGIN TRY
        DECLARE @estado VARCHAR(20) = NULLIF(JSON_VALUE(@json, '$.estado'), '');
        DECLARE @gatipo VARCHAR(20) = NULLIF(JSON_VALUE(@json, '$.gatipo'), '');

        SET @__data = ISNULL((
            SELECT a.IdApDocumentoArchivo AS id,
                   CAST(a.Proveedor AS VARCHAR(20)) AS p,
                   RTRIM(a.ObligacionTipoDocumento) AS t,
                   RTRIM(a.ObligacionNumeroDocumento) AS n
            FROM dbo.MS_AP_DocumentoArchivo a
            LEFT JOIN dbo.MS_AP_DocumentoArchivoTraslado t ON t.IdApDocumentoArchivo = a.IdApDocumentoArchivo
            WHERE (t.IdApDocumentoArchivo IS NOT NULL OR a.Gadatos IS NOT NULL)
              AND (@estado IS NULL OR ISNULL(t.Estado, 'PENDIENTE') = @estado)
              AND (@gatipo IS NULL OR a.Gatipo = @gatipo)
            FOR JSON PATH), '[]');
        EXEC dbo.sp_mig_respuesta 'success', 'Candidatos', @__data;
    END TRY
    BEGIN CATCH
        SET @err = ERROR_MESSAGE();
        EXEC dbo.sp_mig_respuesta 'error', @err, NULL;
    END CATCH
END;
GO

/* ----------------------------------------------------------
   Archivos AP paginados.
   @json: { "estado": ..., "gatipo": ..., "page": 1, "per_page": 25 }
   o bien { "ids": [1,2,3] } para traer filas concretas (filtro OC/OS).
   ---------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_mig_archivos_ap
    @json NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @__data NVARCHAR(MAX);
    DECLARE @err NVARCHAR(500);
    BEGIN TRY
        IF EXISTS (SELECT 1 FROM OPENJSON(@json) WHERE [key] = 'ids')
        BEGIN
            DECLARE @gatipo_ids VARCHAR(20) = NULLIF(JSON_VALUE(@json, '$.gatipo'), '');
            SET @__data = (
                SELECT JSON_QUERY(ISNULL((
                    SELECT a.IdApDocumentoArchivo, a.Gatipo, a.Ganombre,
                           CAST(a.Proveedor AS VARCHAR(20)) AS Proveedor,
                           a.ObligacionTipoDocumento,
                           RTRIM(a.ObligacionNumeroDocumento) AS ObligacionNumeroDocumento,
                           ISNULL(t.Estado, 'PENDIENTE') AS Estado,
                           t.Intentos, t.Etapa, t.FechaPrimerIntento,
                           t.FechaUltimoIntento, t.FechaTraslado, t.RutaDestino,
                           t.IdGrupo, t.ArchivoId, t.MensajeError
                    FROM dbo.MS_AP_DocumentoArchivo a
                    LEFT JOIN dbo.MS_AP_DocumentoArchivoTraslado t ON t.IdApDocumentoArchivo = a.IdApDocumentoArchivo
                    WHERE a.IdApDocumentoArchivo IN (SELECT TRY_CAST(value AS INT) FROM OPENJSON(@json, '$.ids'))
                    ORDER BY a.IdApDocumentoArchivo DESC
                    FOR JSON PATH), '[]')) AS rows,
                       JSON_QUERY(ISNULL((
                    SELECT ISNULL(t.Estado, 'PENDIENTE') AS Estado, COUNT(*) AS total
                    FROM dbo.MS_AP_DocumentoArchivo a
                    LEFT JOIN dbo.MS_AP_DocumentoArchivoTraslado t ON t.IdApDocumentoArchivo = a.IdApDocumentoArchivo
                    WHERE (t.IdApDocumentoArchivo IS NOT NULL OR a.Gadatos IS NOT NULL)
                      AND (@gatipo_ids IS NULL OR a.Gatipo = @gatipo_ids)
                    GROUP BY ISNULL(t.Estado, 'PENDIENTE')
                    ORDER BY COUNT(*) DESC
                    FOR JSON PATH), '[]')) AS por_estado
                FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);
            EXEC dbo.sp_mig_respuesta 'success', 'Archivos AP', @__data;
            RETURN;
        END

        DECLARE @estado VARCHAR(20) = NULLIF(JSON_VALUE(@json, '$.estado'), '');
        DECLARE @gatipo VARCHAR(20) = NULLIF(JSON_VALUE(@json, '$.gatipo'), '');
        DECLARE @page INT = TRY_CAST(JSON_VALUE(@json, '$.page') AS INT);
        DECLARE @per_page INT = TRY_CAST(JSON_VALUE(@json, '$.per_page') AS INT);
        IF @page IS NULL OR @page < 1 SET @page = 1;
        IF @per_page IS NULL OR @per_page < 1 SET @per_page = 25;
        IF @per_page > 200 SET @per_page = 200;
        DECLARE @start INT = (@page - 1) * @per_page + 1;
        DECLARE @end INT = @start + @per_page - 1;

        SET @__data = (
            SELECT
                (SELECT COUNT(*) FROM dbo.MS_AP_DocumentoArchivo a
                 LEFT JOIN dbo.MS_AP_DocumentoArchivoTraslado t ON t.IdApDocumentoArchivo = a.IdApDocumentoArchivo
                 WHERE (t.IdApDocumentoArchivo IS NOT NULL OR a.Gadatos IS NOT NULL)
                   AND (@estado IS NULL OR ISNULL(t.Estado, 'PENDIENTE') = @estado)
                   AND (@gatipo IS NULL OR a.Gatipo = @gatipo)) AS total,
                JSON_QUERY(ISNULL((SELECT * FROM (
                    SELECT a.IdApDocumentoArchivo, a.Gatipo, a.Ganombre,
                           CAST(a.Proveedor AS VARCHAR(20)) AS Proveedor,
                           a.ObligacionTipoDocumento,
                           RTRIM(a.ObligacionNumeroDocumento) AS ObligacionNumeroDocumento,
                           ISNULL(t.Estado, 'PENDIENTE') AS Estado,
                           t.Intentos, t.Etapa, t.FechaPrimerIntento,
                           t.FechaUltimoIntento, t.FechaTraslado, t.RutaDestino,
                           t.IdGrupo, t.ArchivoId, t.MensajeError,
                           ROW_NUMBER() OVER (ORDER BY a.IdApDocumentoArchivo DESC) AS rn
                    FROM dbo.MS_AP_DocumentoArchivo a
                    LEFT JOIN dbo.MS_AP_DocumentoArchivoTraslado t ON t.IdApDocumentoArchivo = a.IdApDocumentoArchivo
                    WHERE (t.IdApDocumentoArchivo IS NOT NULL OR a.Gadatos IS NOT NULL)
                      AND (@estado IS NULL OR ISNULL(t.Estado, 'PENDIENTE') = @estado)
                      AND (@gatipo IS NULL OR a.Gatipo = @gatipo)
                ) base
                WHERE rn BETWEEN @start AND @end
                ORDER BY rn
                FOR JSON PATH), '[]')) AS rows,
                JSON_QUERY(ISNULL((
                    SELECT ISNULL(t.Estado, 'PENDIENTE') AS Estado, COUNT(*) AS total
                    FROM dbo.MS_AP_DocumentoArchivo a
                    LEFT JOIN dbo.MS_AP_DocumentoArchivoTraslado t ON t.IdApDocumentoArchivo = a.IdApDocumentoArchivo
                    WHERE (t.IdApDocumentoArchivo IS NOT NULL OR a.Gadatos IS NOT NULL)
                      AND (@gatipo IS NULL OR a.Gatipo = @gatipo)
                    GROUP BY ISNULL(t.Estado, 'PENDIENTE')
                    ORDER BY COUNT(*) DESC
                    FOR JSON PATH), '[]')) AS por_estado
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);
        EXEC dbo.sp_mig_respuesta 'success', 'Archivos AP', @__data;
    END TRY
    BEGIN CATCH
        SET @err = ERROR_MESSAGE();
        EXEC dbo.sp_mig_respuesta 'error', @err, NULL;
    END CATCH
END;
GO

/* ----------------------------------------------------------
   Archivos MS_Archivo paginados.
   @json: { "estado": ..., "page": 1, "per_page": 25 }
   ---------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_mig_archivos_ms
    @json NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @__data NVARCHAR(MAX);
    DECLARE @err NVARCHAR(500);
    BEGIN TRY
        DECLARE @estado VARCHAR(20) = NULLIF(JSON_VALUE(@json, '$.estado'), '');
        DECLARE @page INT = TRY_CAST(JSON_VALUE(@json, '$.page') AS INT);
        DECLARE @per_page INT = TRY_CAST(JSON_VALUE(@json, '$.per_page') AS INT);
        IF @page IS NULL OR @page < 1 SET @page = 1;
        IF @per_page IS NULL OR @per_page < 1 SET @per_page = 25;
        IF @per_page > 200 SET @per_page = 200;
        DECLARE @start INT = (@page - 1) * @per_page + 1;
        DECLARE @end INT = @start + @per_page - 1;

        SET @__data = (
            SELECT
                (SELECT COUNT(*) FROM dbo.MS_Archivo a
                 LEFT JOIN dbo.MS_ArchivoTraslado t ON t.IdArchivo = a.IdArchivo
                 WHERE (@estado IS NULL OR ISNULL(t.Estado, 'PENDIENTE') = @estado)) AS total,
                JSON_QUERY(ISNULL((SELECT * FROM (
                    SELECT CONVERT(VARCHAR(36), a.IdArchivo) AS IdArchivo,
                           a.Tabla, a.Id AS IdRegistro, a.Nombre,
                           RTRIM(a.Empresa) AS Empresa,
                           a.Tipo AS Mime, ISNULL(t.Estado, 'PENDIENTE') AS Estado,
                           t.Intentos, t.Etapa,
                           t.FechaTraslado, t.RutaDestino, t.ArchivoId, t.MensajeError,
                           ROW_NUMBER() OVER (ORDER BY t.FechaTraslado DESC) AS rn
                    FROM dbo.MS_Archivo a
                    LEFT JOIN dbo.MS_ArchivoTraslado t ON t.IdArchivo = a.IdArchivo
                    WHERE (@estado IS NULL OR ISNULL(t.Estado, 'PENDIENTE') = @estado)
                ) base
                WHERE rn BETWEEN @start AND @end
                ORDER BY rn
                FOR JSON PATH), '[]')) AS rows,
                JSON_QUERY(ISNULL((
                    SELECT ISNULL(t.Estado, 'PENDIENTE') AS Estado, COUNT(*) AS total
                    FROM dbo.MS_Archivo a
                    LEFT JOIN dbo.MS_ArchivoTraslado t ON t.IdArchivo = a.IdArchivo
                    GROUP BY ISNULL(t.Estado, 'PENDIENTE')
                    ORDER BY COUNT(*) DESC
                    FOR JSON PATH), '[]')) AS por_estado
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);
        EXEC dbo.sp_mig_respuesta 'success', 'Archivos MS', @__data;
    END TRY
    BEGIN CATCH
        SET @err = ERROR_MESSAGE();
        EXEC dbo.sp_mig_respuesta 'error', @err, NULL;
    END CATCH
END;
GO

/* ----------------------------------------------------------
   Preview binario (rowset, no JSON).
   ---------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_mig_preview_ap
    @json NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @id INT = TRY_CAST(JSON_VALUE(@json, '$.id') AS INT);
    SELECT Ganombre, Gadatos
    FROM dbo.MS_AP_DocumentoArchivo
    WHERE IdApDocumentoArchivo = @id;
END;
GO

CREATE OR ALTER PROCEDURE dbo.sp_mig_preview_ms
    @json NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @id UNIQUEIDENTIFIER = TRY_CAST(JSON_VALUE(@json, '$.id') AS UNIQUEIDENTIFIER);
    SELECT a.Nombre, a.Contenido, a.Tipo, t.Estado, t.RutaDestino
    FROM dbo.MS_Archivo a
    LEFT JOIN dbo.MS_ArchivoTraslado t ON t.IdArchivo = a.IdArchivo
    WHERE a.IdArchivo = @id;
END;
GO

/* ----------------------------------------------------------
   Pendientes globales (para "Procesar todo lo faltante").
   ---------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_mig_pendientes
    @json NVARCHAR(MAX) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @__data NVARCHAR(MAX);
    DECLARE @err NVARCHAR(500);
    BEGIN TRY
        SET @__data = (
            SELECT
                (SELECT COUNT(*) FROM dbo.MS_Archivo a
                 LEFT JOIN dbo.MS_ArchivoTraslado t ON t.IdArchivo = a.IdArchivo
                 WHERE t.IdArchivo IS NULL OR t.Estado IN ('PENDIENTE', 'ERROR')) AS ms,
                (SELECT COUNT(*) FROM dbo.MS_AP_DocumentoArchivo a
                 LEFT JOIN dbo.MS_AP_DocumentoArchivoTraslado t ON t.IdApDocumentoArchivo = a.IdApDocumentoArchivo
                 WHERE a.Gadatos IS NOT NULL AND (t.IdApDocumentoArchivo IS NULL OR t.Estado IN ('PENDIENTE', 'ERROR'))) AS ap
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);
        EXEC dbo.sp_mig_respuesta 'success', 'Pendientes', @__data;
    END TRY
    BEGIN CATCH
        SET @err = ERROR_MESSAGE();
        EXEC dbo.sp_mig_respuesta 'error', @err, NULL;
    END CATCH
END;
GO

/* ----------------------------------------------------------
   Pendientes de una orden OC/OS.
   @json: { "numero": "0000001710", "tipo": "OC"|"SO"|null,
            "obligaciones": [ {"p":"1525","t":"FA","n":"F007-0002740"} ] }
   Cuenta AP por claves de obligacion y MS por Tabla+Id
   (WH_OrdenCompra=OC, Compromiso=SO, Obligaciones="p|t|n").
   ---------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_mig_pendientes_orden
    @json NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @__data NVARCHAR(MAX);
    DECLARE @err NVARCHAR(500);
    BEGIN TRY
        DECLARE @numero NVARCHAR(50) = JSON_VALUE(@json, '$.numero');
        DECLARE @tipo VARCHAR(5) = NULLIF(JSON_VALUE(@json, '$.tipo'), '');

        DECLARE @obls TABLE (p VARCHAR(20), t VARCHAR(5), n VARCHAR(30),
                             id_obl NVARCHAR(400));
        INSERT INTO @obls (p, t, n, id_obl)
        SELECT p, t, n, p + '|' + t + '|' + n
        FROM OPENJSON(@json, '$.obligaciones')
        WITH (p VARCHAR(20) '$.p', t VARCHAR(5) '$.t', n VARCHAR(30) '$.n');

        DECLARE @ap INT = (
            SELECT COUNT(*)
            FROM dbo.MS_AP_DocumentoArchivo a
            LEFT JOIN dbo.MS_AP_DocumentoArchivoTraslado t ON t.IdApDocumentoArchivo = a.IdApDocumentoArchivo
            WHERE a.Gadatos IS NOT NULL
              AND (t.IdApDocumentoArchivo IS NULL OR t.Estado IN ('PENDIENTE', 'ERROR'))
              AND EXISTS (SELECT 1 FROM @obls o
                          WHERE CAST(a.Proveedor AS VARCHAR(20)) = o.p
                            AND RTRIM(a.ObligacionTipoDocumento) = o.t
                            AND RTRIM(a.ObligacionNumeroDocumento) = o.n));

        DECLARE @ms INT = (
            SELECT COUNT(*)
            FROM dbo.MS_Archivo a
            LEFT JOIN dbo.MS_ArchivoTraslado t ON t.IdArchivo = a.IdArchivo
            WHERE a.Contenido IS NOT NULL
              AND (t.IdArchivo IS NULL OR t.Estado IN ('PENDIENTE', 'ERROR'))
              AND (
                   ((@tipo IS NULL OR @tipo = 'OC') AND a.Tabla = 'WH_OrdenCompra' AND a.Id = @numero)
                OR ((@tipo IS NULL OR @tipo = 'SO') AND a.Tabla = 'Compromiso' AND a.Id = @numero)
                OR (a.Tabla = 'Obligaciones' AND a.Id IN (SELECT id_obl FROM @obls))
              ));

        SET @__data = (SELECT @ap AS ap, @ms AS ms, @ap + @ms AS total
                       FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);
        EXEC dbo.sp_mig_respuesta 'success', 'Pendientes de orden', @__data;
    END TRY
    BEGIN CATCH
        SET @err = ERROR_MESSAGE();
        EXEC dbo.sp_mig_respuesta 'error', @err, NULL;
    END CATCH
END;
GO

/* ----------------------------------------------------------
   Actualizar los lotes del entorno (usado por run/all).
   @json: { "entorno": "PRUEBA", "BATCH_PRUEBA": "10",
            "BATCH_AP_DOCUMENTO_PRUEBA": "500" }
   ---------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_mig_actualizar_lotes
    @json NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @err NVARCHAR(500);
    BEGIN TRY
        DECLARE @entorno VARCHAR(20) = JSON_VALUE(@json, '$.entorno');
        UPDATE dbo.Entornos_Migracion
        SET BATCH_PRUEBA = ISNULL(JSON_VALUE(@json, '$.BATCH_PRUEBA'), BATCH_PRUEBA),
            BATCH_AP_DOCUMENTO_PRUEBA = ISNULL(JSON_VALUE(@json, '$.BATCH_AP_DOCUMENTO_PRUEBA'), BATCH_AP_DOCUMENTO_PRUEBA)
        WHERE Entorno = @entorno;
        EXEC dbo.sp_mig_respuesta 'success', 'Lotes actualizados', NULL;
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
    GRANT EXECUTE ON dbo.sp_mig_respuesta         TO usr_migracion_adjuntos;
    GRANT EXECUTE ON dbo.sp_mig_entornos          TO usr_migracion_adjuntos;
    GRANT EXECUTE ON dbo.sp_mig_entorno_activo    TO usr_migracion_adjuntos;
    GRANT EXECUTE ON dbo.sp_mig_config            TO usr_migracion_adjuntos;
    GRANT EXECUTE ON dbo.sp_mig_stats             TO usr_migracion_adjuntos;
    GRANT EXECUTE ON dbo.sp_mig_candidatos_ap     TO usr_migracion_adjuntos;
    GRANT EXECUTE ON dbo.sp_mig_archivos_ap       TO usr_migracion_adjuntos;
    GRANT EXECUTE ON dbo.sp_mig_archivos_ms       TO usr_migracion_adjuntos;
    GRANT EXECUTE ON dbo.sp_mig_preview_ap        TO usr_migracion_adjuntos;
    GRANT EXECUTE ON dbo.sp_mig_preview_ms        TO usr_migracion_adjuntos;
    GRANT EXECUTE ON dbo.sp_mig_pendientes        TO usr_migracion_adjuntos;
    GRANT EXECUTE ON dbo.sp_mig_pendientes_orden  TO usr_migracion_adjuntos;
    GRANT EXECUTE ON dbo.sp_mig_actualizar_lotes  TO usr_migracion_adjuntos;
END;
GO
