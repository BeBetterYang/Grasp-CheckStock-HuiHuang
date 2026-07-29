using System.Data;
using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<Db>();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", () => Results.Ok(new { ok = true, time = DateTime.Now }));

app.MapPost("/api/login", async (Db db, LoginRequest request) =>
{
    var login = (request.Login ?? "").Trim();
    var password = request.Password ?? "";
    if (login.Length == 0)
    {
        return Results.BadRequest(new { message = "请输入操作员" });
    }

    await using var conn = await db.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        SELECT TOP 1 l.etypeid, ISNULL(e.efullname, l.etypeid) AS efullname, ISNULL(e.eusercode, l.etypeid) AS eusercode
        FROM loginuser l
        LEFT JOIN employee e ON e.etypeid = l.etypeid
        WHERE l.password = @password
          AND (
                l.etypeid = @login
             OR ISNULL(e.eusercode, '') = @login
             OR ISNULL(e.efullname, '') = @login
             OR ISNULL(e.ename, '') = @login
          )
          AND ISNULL(e.deleted, 0) = 0
          AND ISNULL(e.isStop, 0) = 0
        ORDER BY CASE WHEN l.etypeid = @login THEN 0 ELSE 1 END
        """;
    cmd.Parameters.AddWithValue("@login", login);
    cmd.Parameters.AddWithValue("@password", password);

    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
    {
        return Results.Unauthorized();
    }

    return Results.Ok(new OperatorDto(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2)));
});

app.MapGet("/api/warehouses", async (Db db) =>
{
    await using var conn = await db.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        SELECT ktypeid, kusercode, kfullname, kname
        FROM Stock
        WHERE deleted = 0 AND isStop = 0 AND soncount = 0 AND ktypeid <> '00000'
        ORDER BY rowindex, ktypeid
        """;

    var rows = new List<WarehouseDto>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        rows.Add(new WarehouseDto(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3)));
    }

    return Results.Ok(rows);
});

app.MapGet("/api/check-session/{ktypeid}", async (Db db, string ktypeid) =>
{
    await using var conn = await db.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        EXEC CodexPda_GetWarehouseCheck @KTypeID
        """;
    cmd.Parameters.AddWithValue("@KTypeID", ktypeid);

    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
    {
        return Results.Ok(new WarehouseCheckDto(false, false, null, null, "该仓库没有已创建的盘点单"));
    }

    var mode = reader.GetInt32(reader.GetOrdinal("CHECKEDMODE"));
    var updateTag = HasColumn(reader, "UpdateTag") ? ToInt(reader["UpdateTag"]) : 0;
    var date = reader.GetString(reader.GetOrdinal("Date"));
    var warehouseName = reader.GetString(reader.GetOrdinal("kfullname"));
    var ended = IsCheckEnded(mode);
    return Results.Ok(new WarehouseCheckDto(true, ended, date, warehouseName, ended ? "该仓库盘点已结束，不允许继续盘点" : null, updateTag));
});

app.MapGet("/api/goods/search", async (Db db, [FromQuery] string q, [FromQuery] string ktypeid) =>
{
    var sw = Stopwatch.StartNew();
    q = (q ?? "").Trim();
    if (q.Length == 0)
    {
        return Results.Ok(Array.Empty<GoodsSearchDto>());
    }

    await using var conn = await db.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        SELECT TOP 30 p.ptypeid, p.prec, p.pusercode, p.pfullname, p.pname, p.costmode,
               p.pgManCode, p.SNManCode, p.KWManCode, p.PJobManCode, p.UsefulLifeMonth, p.UsefulLifeDay,
               p.punitname, p.pgholunit, p.pgholunitrate,
               CASE WHEN p.pgManCode <> 0 OR p.PJobManCode <> 0 OR EXISTS (
                    SELECT 1 FROM GoodsStocks gs
                    WHERE gs.PtypeId = p.ptypeid AND gs.KtypeId = @ktypeid
                      AND (ISNULL(gs.GoodsBatchID, '') <> '' OR ISNULL(gs.JobNumber, '') <> '')
               ) THEN 1 ELSE 0 END AS hasBatch
        FROM ptype p
        WHERE p.deleted = 0 AND p.isStop = 0 AND p.soncount = 0 AND p.ptypetype = 0
          AND (
               p.pusercode LIKE @like OR p.pfullname LIKE @like OR p.pname LIKE @like OR p.pnamepy LIKE @like
               OR EXISTS (SELECT 1 FROM xw_PtypeBarCode b WHERE b.PTypeId = p.ptypeid AND b.BarCode = @q)
               OR p.ptypeid = @q
          )
        ORDER BY CASE WHEN p.pusercode = @q THEN 0 WHEN p.ptypeid = @q THEN 1 ELSE 2 END, p.pusercode
        """;
    cmd.Parameters.AddWithValue("@q", q);
    cmd.Parameters.AddWithValue("@like", "%" + q + "%");
    cmd.Parameters.AddWithValue("@ktypeid", ktypeid);

    var rows = new List<GoodsSearchDto>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        var ptypeid = reader.GetString(0);
        rows.Add(new GoodsSearchDto(
            ptypeid,
            reader.GetInt32(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt32(5),
            ToBool(reader[6]),
            ToBool(reader[7]),
            ToBool(reader[8]),
            ToBool(reader[9]),
            ToInt(reader[10]),
            ToInt(reader[11]),
            reader.GetString(12),
            reader.GetString(13),
            reader.GetDecimal(14),
            reader.GetInt32(15) == 1,
            await LoadUnits(conn, ptypeid)));
    }

    app.Logger.LogInformation("Goods search q={Query} rows={Rows} elapsed={ElapsedMs}ms", q, rows.Count, sw.ElapsedMilliseconds);
    return Results.Ok(rows);
});

app.MapGet("/api/goods/scan", async (Db db, [FromQuery] string q, [FromQuery] string ktypeid, [FromQuery] string date, [FromQuery] string etypeid) =>
{
    var totalSw = Stopwatch.StartNew();
    q = (q ?? "").Trim();
    if (q.Length == 0)
    {
        return Results.Ok(new GoodsScanDto(null, [], 0, 0, 0));
    }

    await using var conn = await db.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        SELECT TOP 2 p.ptypeid, p.prec, p.pusercode, p.pfullname, p.pname, p.costmode,
               p.pgManCode, p.SNManCode, p.KWManCode, p.PJobManCode, p.UsefulLifeMonth, p.UsefulLifeDay,
               p.punitname, p.pgholunit, p.pgholunitrate,
               CASE WHEN p.pgManCode <> 0 OR p.PJobManCode <> 0 OR EXISTS (
                    SELECT 1 FROM GoodsStocks gs
                    WHERE gs.PtypeId = p.ptypeid AND gs.KtypeId = @ktypeid
                      AND (ISNULL(gs.GoodsBatchID, '') <> '' OR ISNULL(gs.JobNumber, '') <> '')
               ) THEN 1 ELSE 0 END AS hasBatch
        FROM ptype p
        WHERE p.deleted = 0 AND p.isStop = 0 AND p.soncount = 0 AND p.ptypetype = 0
          AND EXISTS (SELECT 1 FROM xw_PtypeBarCode b WHERE b.PTypeId = p.ptypeid AND b.BarCode = @q)
        ORDER BY p.pusercode, p.ptypeid
        """;
    cmd.Parameters.AddWithValue("@q", q);
    cmd.Parameters.AddWithValue("@ktypeid", ktypeid);

    var searchSw = Stopwatch.StartNew();
    var rows = new List<GoodsSearchDto>();
    await using (var reader = await cmd.ExecuteReaderAsync())
    {
        while (await reader.ReadAsync())
        {
            var ptypeid = reader.GetString(0);
            rows.Add(new GoodsSearchDto(
                ptypeid,
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5),
                ToBool(reader[6]),
                ToBool(reader[7]),
                ToBool(reader[8]),
                ToBool(reader[9]),
                ToInt(reader[10]),
                ToInt(reader[11]),
                reader.GetString(12),
                reader.GetString(13),
                reader.GetDecimal(14),
                reader.GetInt32(15) == 1,
                []));
        }
    }
    searchSw.Stop();

    for (var i = 0; i < rows.Count; i++)
    {
        rows[i] = rows[i] with { Units = await LoadUnits(conn, rows[i].PTypeID) };
    }

    if (rows.Count != 1)
    {
        app.Logger.LogInformation("Goods scan q={Query} exactRows={Rows} search={SearchElapsedMs}ms total={TotalElapsedMs}ms", q, rows.Count, searchSw.ElapsedMilliseconds, totalSw.ElapsedMilliseconds);
        return Results.Ok(new GoodsScanDto(null, [], searchSw.ElapsedMilliseconds, 0, totalSw.ElapsedMilliseconds));
    }

    var stockSw = Stopwatch.StartNew();
    var stockRows = await LoadStockRows(conn, ktypeid, rows[0].PTypeID, date, string.IsNullOrWhiteSpace(etypeid) ? "00000" : etypeid);
    stockSw.Stop();
    totalSw.Stop();
    app.Logger.LogInformation("Goods scan q={Query} ptypeid={PTypeID} search={SearchElapsedMs}ms stock={StockElapsedMs}ms total={TotalElapsedMs}ms", q, rows[0].PTypeID, searchSw.ElapsedMilliseconds, stockSw.ElapsedMilliseconds, totalSw.ElapsedMilliseconds);
    return Results.Ok(new GoodsScanDto(rows[0], stockRows, searchSw.ElapsedMilliseconds, stockSw.ElapsedMilliseconds, totalSw.ElapsedMilliseconds));
});

app.MapGet("/api/goods/categories", async (Db db) =>
{
    await using var conn = await db.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        SELECT '00000' AS ptypeid, '全部分类' AS pfullname, 0 AS rowindex
        UNION ALL
        SELECT ptypeid, pfullname, rowindex
        FROM ptype
        WHERE deleted = 0 AND isStop = 0 AND ptypetype = 0 AND soncount > 0 AND ParId = '00000'
        ORDER BY rowindex, ptypeid
        """;

    var rows = new List<ProductCategoryDto>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        rows.Add(new ProductCategoryDto(
            reader.GetString(0),
            reader.GetString(1)));
    }

    return Results.Ok(rows);
});

app.MapGet("/api/goods/list", async (Db db, [FromQuery] string categoryId, [FromQuery] string? q, [FromQuery] string ktypeid, [FromQuery] string date, [FromQuery] string etypeid) =>
{
    categoryId = string.IsNullOrWhiteSpace(categoryId) ? "00000" : categoryId.Trim();
    q = (q ?? "").Trim();
    await using var conn = await db.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        SELECT TOP 40 p.ptypeid, p.prec, p.pusercode, p.pfullname, p.pname, p.costmode,
               p.pgManCode, p.SNManCode, p.KWManCode, p.PJobManCode, p.UsefulLifeMonth, p.UsefulLifeDay,
               p.punitname, p.pgholunit, p.pgholunitrate,
               CASE WHEN p.pgManCode <> 0 OR p.PJobManCode <> 0 OR EXISTS (
                    SELECT 1 FROM GoodsStocks gs
                    WHERE gs.PtypeId = p.ptypeid AND gs.KtypeId = @ktypeid
                      AND (ISNULL(gs.GoodsBatchID, '') <> '' OR ISNULL(gs.JobNumber, '') <> '')
               ) THEN 1 ELSE 0 END AS hasBatch
        FROM ptype p
        WHERE p.deleted = 0 AND p.isStop = 0 AND p.soncount = 0 AND p.ptypetype = 0
          AND (@categoryId = '00000' OR p.ptypeid LIKE @categoryLike)
          AND (
               @q = ''
               OR p.pusercode LIKE @like OR p.pfullname LIKE @like OR p.pname LIKE @like OR p.pnamepy LIKE @like
               OR EXISTS (SELECT 1 FROM xw_PtypeBarCode b WHERE b.PTypeId = p.ptypeid AND b.BarCode = @q)
          )
        ORDER BY p.rowindex, p.pusercode, p.ptypeid
        """;
    cmd.Parameters.AddWithValue("@categoryId", categoryId);
    cmd.Parameters.AddWithValue("@categoryLike", categoryId + "%");
    cmd.Parameters.AddWithValue("@q", q);
    cmd.Parameters.AddWithValue("@like", "%" + q + "%");
    cmd.Parameters.AddWithValue("@ktypeid", ktypeid);

    var basics = new List<GoodsSearchDto>();
    await using (var reader = await cmd.ExecuteReaderAsync())
    {
        while (await reader.ReadAsync())
        {
            var ptypeid = reader.GetString(0);
            basics.Add(new GoodsSearchDto(
                ptypeid,
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5),
                ToBool(reader[6]),
                ToBool(reader[7]),
                ToBool(reader[8]),
                ToBool(reader[9]),
                ToInt(reader[10]),
                ToInt(reader[11]),
                reader.GetString(12),
                reader.GetString(13),
                reader.GetDecimal(14),
                reader.GetInt32(15) == 1,
                await LoadUnits(conn, ptypeid)));
        }
    }

    var rows = new List<GoodsListDto>();
    foreach (var goods in basics)
    {
        var stockRows = await LoadStockRows(conn, ktypeid, goods.PTypeID, date, string.IsNullOrWhiteSpace(etypeid) ? "00000" : etypeid);
        rows.Add(new GoodsListDto(
            goods,
            stockRows.Sum(s => s.StockQty),
            stockRows.Sum(s => s.StockPgHolInqty),
            stockRows.Count));
    }

    return Results.Ok(rows);
});

app.MapGet("/api/goods/{ptypeid}/stock", async (Db db, string ptypeid, [FromQuery] string ktypeid, [FromQuery] string date, [FromQuery] string etypeid) =>
{
    var sw = Stopwatch.StartNew();
    await using var conn = await db.OpenAsync();
    var rows = await LoadStockRows(conn, ktypeid, ptypeid, date, string.IsNullOrWhiteSpace(etypeid) ? "00000" : etypeid);
    app.Logger.LogInformation("Goods stock ptypeid={PTypeID} rows={Rows} elapsed={ElapsedMs}ms", ptypeid, rows.Count, sw.ElapsedMilliseconds);
    return Results.Ok(rows);
});

app.MapPost("/api/submissions", async (Db db, SubmissionRequest request) =>
{
    if (request.Items.Count == 0)
    {
        return Results.BadRequest(new { message = "没有可提交的盘点商品" });
    }

    await using var conn = await db.OpenAsync();
    await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
    try
    {
        var check = await GetWarehouseCheck(conn, tx, request.KTypeID);
        if (check is null)
        {
            return Results.BadRequest(new { message = "该仓库没有已创建的盘点单" });
        }
        if (check.Ended)
        {
            return Results.BadRequest(new { message = "该仓库盘点已结束，不允许继续盘点" });
        }
        if (check.UpdateTag == 0)
        {
            return Results.BadRequest(new { message = "未读取到当前盘点单标识，不能保存" });
        }
        if (!string.Equals(check.Date, request.CheckDate, StringComparison.Ordinal))
        {
            return Results.BadRequest(new { message = $"盘点日期已变化，请重新选择仓库。当前日期：{check.Date}" });
        }

        var headerId = await InsertScalarInt(conn, tx, """
            INSERT CodexPdaCheckHeader(KTypeID, CheckDate, ETypeID, Status, Remark, CreatedAt)
            VALUES(@KTypeID, @CheckDate, @ETypeID, 'Submitted', @Remark, GETDATE());
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            ("@KTypeID", request.KTypeID),
            ("@CheckDate", request.CheckDate),
            ("@ETypeID", request.ETypeID),
            ("@Remark", request.Remark ?? ""));

        var submitId = await InsertScalarInt(conn, tx, """
            INSERT CodexPdaCheckSubmit(HeaderID, KTypeID, CheckDate, ETypeID, SubmittedAt, ItemCount, BatchCount)
            VALUES(@HeaderID, @KTypeID, @CheckDate, @ETypeID, GETDATE(), @ItemCount, @BatchCount);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            ("@HeaderID", headerId),
            ("@KTypeID", request.KTypeID),
            ("@CheckDate", request.CheckDate),
            ("@ETypeID", request.ETypeID),
            ("@ItemCount", request.Items.Count),
            ("@BatchCount", request.Items.Sum(i => Math.Max(1, i.Batches.Count))));

        var saveRows = new List<PreparedCheckRow>();
        var detailCount = 0;
        foreach (var item in request.Items)
        {
            var units = await LoadUnits(conn, item.PTypeID, tx);
            var unit = units.FirstOrDefault(u => u.Ordid == item.UnitOrdid) ?? units.FirstOrDefault(u => u.IsBase) ?? new UnitDto("0", "", 1, true);
            var goods = await LoadGoodsSaveInfo(conn, tx, item.PTypeID);
            var batches = item.Batches.Count == 0
                ? [new SubmissionBatchRequest("", 0, "", "", "", item.StockQty, 0, item.CountQty, 0, false)]
                : item.Batches;

            foreach (var batch in batches.Where(b => !b.Deleted))
            {
                var resolvedBatch = await ResolveBatchIdentity(conn, tx, item.PTypeID, request.KTypeID, batch);
                var checkedBaseQty = Math.Round(batch.CountQty * unit.Rate, 10);
                var profitQty = checkedBaseQty - batch.StockQty;
                var detailId = await InsertScalarInt(conn, tx, """
                    INSERT CodexPdaCheckSubmitDetail
                    (SubmitID, PTypeID, UnitOrdid, UnitName, UnitRate, GoodsBatchID, GoodsOrderID, JobNumber, OutFactoryDate, UsefulEndDate,
                     StockQty, StockPgHolInqty, CheckedQty, CheckedBaseQty, ProfitQty, IsNew, CreatedAt)
                    VALUES
                    (@SubmitID, @PTypeID, @UnitOrdid, @UnitName, @UnitRate, @GoodsBatchID, @GoodsOrderID, @JobNumber, @OutFactoryDate, @UsefulEndDate,
                     @StockQty, @StockPgHolInqty, @CheckedQty, @CheckedBaseQty, @ProfitQty, @IsNew, GETDATE());
                    SELECT CAST(SCOPE_IDENTITY() AS int);
                    """,
                    ("@SubmitID", submitId),
                    ("@PTypeID", item.PTypeID),
                    ("@UnitOrdid", unit.Ordid),
                    ("@UnitName", unit.Name),
                    ("@UnitRate", unit.Rate),
                    ("@GoodsBatchID", resolvedBatch.GoodsBatchID),
                    ("@GoodsOrderID", resolvedBatch.GoodsOrderID),
                    ("@JobNumber", batch.JobNumber ?? ""),
                    ("@OutFactoryDate", batch.OutFactoryDate ?? ""),
                    ("@UsefulEndDate", batch.UsefulEndDate ?? ""),
                    ("@StockQty", batch.StockQty),
                    ("@StockPgHolInqty", batch.StockPgHolInqty),
                    ("@CheckedQty", batch.CountQty),
                    ("@CheckedBaseQty", checkedBaseQty),
                    ("@ProfitQty", profitQty),
                    ("@IsNew", batch.IsNew ? 1 : 0));

                saveRows.Add(new PreparedCheckRow(
                    detailId,
                    item.PTypeID,
                    goods.Prec,
                    goods.CostMode,
                    goods.PgManCode,
                    goods.SnManCode,
                    goods.KwManCode,
                    resolvedBatch.GoodsOrderID,
                    checkedBaseQty,
                    batch.StockQty,
                    batch.StockPgHolInqty,
                    batch.JobNumber ?? "",
                    batch.OutFactoryDate ?? "",
                    batch.UsefulEndDate ?? "",
                    resolvedBatch.GoodsBatchID,
                    batch.IsNew));
                detailCount++;
            }
        }

        await SaveCheckedCountByErpProcedure(conn, tx, request, check.UpdateTag, saveRows);
        foreach (var row in saveRows)
        {
            var checkedCountId = await FindCheckedCountId(conn, tx, request, check.UpdateTag, row);
            if (checkedCountId <= 0)
            {
                throw new InvalidOperationException($"ERP已保存但未能定位CheckedCount明细：{row.PTypeID}");
            }
            await Execute(conn, tx, """
                INSERT CodexPdaCheckedCountMap(SubmitDetailID, CheckedCountID, UpdateTag)
                VALUES(@SubmitDetailID, @CheckedCountID, @UpdateTag)
                """,
                ("@SubmitDetailID", row.DetailID),
                ("@CheckedCountID", checkedCountId),
                ("@UpdateTag", check.UpdateTag));
        }

        await tx.CommitAsync();
        return Results.Ok(new { submitId, headerId, detailCount });
    }
    catch (Exception ex)
    {
        await tx.RollbackAsync();
        return Results.Problem(ex.Message);
    }
});

app.MapGet("/api/history", async (Db db, [FromQuery] string? ktypeid, [FromQuery] string? etypeid) =>
{
    await using var conn = await db.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        SELECT TOP 100 s.SubmitID, s.KTypeID, ISNULL(st.kfullname, s.KTypeID) AS kfullname, s.CheckDate,
               s.ETypeID, ISNULL(e.efullname, s.ETypeID) AS efullname, s.SubmittedAt, s.ItemCount, s.BatchCount
        FROM CodexPdaCheckSubmit s
        LEFT JOIN Stock st ON st.ktypeid = s.KTypeID
        LEFT JOIN employee e ON e.etypeid = s.ETypeID
        WHERE (@KTypeID = '' OR s.KTypeID = @KTypeID)
          AND (@ETypeID = '' OR s.ETypeID = @ETypeID)
        ORDER BY s.SubmittedAt DESC, s.SubmitID DESC
        """;
    cmd.Parameters.AddWithValue("@KTypeID", ktypeid ?? "");
    cmd.Parameters.AddWithValue("@ETypeID", etypeid ?? "");

    var rows = new List<HistoryDto>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        rows.Add(new HistoryDto(
            reader.GetInt32(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetDateTime(6),
            reader.GetInt32(7),
            reader.GetInt32(8)));
    }

    return Results.Ok(rows);
});

app.MapGet("/api/history/{submitId:int}", async (Db db, int submitId) =>
{
    await using var conn = await db.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        SELECT d.DetailID, d.PTypeID, ISNULL(p.pusercode, '') AS pusercode, ISNULL(p.pfullname, d.PTypeID) AS pfullname,
               d.UnitOrdid, d.UnitName, d.UnitRate, d.GoodsBatchID, d.GoodsOrderID, d.JobNumber, d.OutFactoryDate, d.UsefulEndDate,
               d.StockQty, d.CheckedQty, d.CheckedBaseQty, d.ProfitQty, d.IsNew
        FROM CodexPdaCheckSubmitDetail d
        LEFT JOIN ptype p ON p.ptypeid = d.PTypeID
        WHERE d.SubmitID = @SubmitID
        ORDER BY d.DetailID
        """;
    cmd.Parameters.AddWithValue("@SubmitID", submitId);

    var rows = new List<HistoryDetailDto>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        rows.Add(new HistoryDetailDto(
            reader.GetInt32(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetDecimal(6),
            reader.GetString(7),
            reader.GetInt32(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetString(11),
            reader.GetDecimal(12),
            reader.GetDecimal(13),
            reader.GetDecimal(14),
            reader.GetDecimal(15),
            reader.GetBoolean(16)));
    }

    return Results.Ok(rows);
});

app.MapDelete("/api/history/{submitId:int}", DeletePdaHistory);
app.MapPost("/api/history/{submitId:int}/delete", DeletePdaHistory);

app.Run();

static async Task<IResult> DeletePdaHistory(Db db, int submitId)
{
    await using var conn = await db.OpenAsync();
    await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
    try
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            DECLARE @HeaderID int;

            SELECT @HeaderID = HeaderID
            FROM CodexPdaCheckSubmit
            WHERE SubmitID = @SubmitID;

            IF @HeaderID IS NULL
            BEGIN
                SELECT CAST(0 AS int) AS DeletedMap,
                       CAST(0 AS int) AS DeletedDetail,
                       CAST(0 AS int) AS DeletedSubmit,
                       CAST(0 AS int) AS DeletedHeader;
                RETURN;
            END

            DELETE m
            FROM CodexPdaCheckedCountMap m
            INNER JOIN CodexPdaCheckSubmitDetail d ON d.DetailID = m.SubmitDetailID
            WHERE d.SubmitID = @SubmitID;
            DECLARE @DeletedMap int = @@ROWCOUNT;

            DELETE FROM CodexPdaCheckSubmitDetail WHERE SubmitID = @SubmitID;
            DECLARE @DeletedDetail int = @@ROWCOUNT;

            DELETE FROM CodexPdaCheckSubmit WHERE SubmitID = @SubmitID;
            DECLARE @DeletedSubmit int = @@ROWCOUNT;

            DELETE FROM CodexPdaCheckHeader
            WHERE HeaderID = @HeaderID
              AND NOT EXISTS (SELECT 1 FROM CodexPdaCheckSubmit WHERE HeaderID = @HeaderID);
            DECLARE @DeletedHeader int = @@ROWCOUNT;

            SELECT @DeletedMap AS DeletedMap,
                   @DeletedDetail AS DeletedDetail,
                   @DeletedSubmit AS DeletedSubmit,
                   @DeletedHeader AS DeletedHeader;
            """;
        cmd.Parameters.AddWithValue("@SubmitID", submitId);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        var deletedMap = reader.GetInt32(0);
        var deletedDetail = reader.GetInt32(1);
        var deletedSubmit = reader.GetInt32(2);
        var deletedHeader = reader.GetInt32(3);

        await tx.CommitAsync();
        return Results.Ok(new { deleted = deletedSubmit > 0, submitId, deletedMap, deletedDetail, deletedSubmit, deletedHeader });
    }
    catch (Exception ex)
    {
        await tx.RollbackAsync();
        return Results.Problem(ex.Message);
    }
}

static async Task<GoodsSaveInfo> LoadGoodsSaveInfo(SqlConnection conn, SqlTransaction tx, string ptypeid)
{
    await using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = """
        SELECT prec, costmode, pgManCode, SNManCode, KWManCode
        FROM ptype
        WHERE ptypeid = @PTypeID
        """;
    cmd.Parameters.AddWithValue("@PTypeID", ptypeid);
    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
    {
        throw new InvalidOperationException($"商品不存在：{ptypeid}");
    }

    return new GoodsSaveInfo(
        ToInt(reader["prec"]),
        ToInt(reader["costmode"]),
        ToInt(reader["pgManCode"]),
        ToInt(reader["SNManCode"]),
        ToInt(reader["KWManCode"]));
}

static async Task SaveCheckedCountByErpProcedure(SqlConnection conn, SqlTransaction tx, SubmissionRequest request, int updateTag, List<PreparedCheckRow> rows)
{
    if (rows.Count == 0)
    {
        return;
    }

    var guid = "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}";
    var split = await ExecuteScalarString(conn, tx, "SELECT dbo.f_SaveBillChar()");
    string Join<T>(IEnumerable<T> values) => string.Join(split, values.Select(value => FormatSaveValue(value))) + split;

    var tempRows = new (string Name, string Value)[]
    {
        ("@szPtypeid", Join(rows.Select(r => r.Prec))),
        ("@szKWtypeid", Join(rows.Select(_ => ""))),
        ("@szsltypeid", Join(rows.Select(_ => ""))),
        ("@dQty", Join(rows.Select(r => r.CheckedBaseQty))),
        ("@lCostMode", Join(rows.Select(r => r.CostMode))),
        ("@StockQty", Join(rows.Select(r => r.StockQty))),
        ("@checkSideQty", Join(rows.Select(_ => 0))),
        ("@pgholqty", Join(rows.Select(_ => 0))),
        ("@nUpdateTag", Join(rows.Select(_ => updateTag))),
        ("@JobNumber", Join(rows.Select(r => r.JobNumber))),
        ("@OutFactoryDate", Join(rows.Select(r => r.OutFactoryDate))),
        ("@GoodsBatchID", Join(rows.Select(r => r.GoodsBatchID))),
        ("@TotaL", Join(rows.Select(_ => 0))),
        ("@Sqlpgholnqty", Join(rows.Select(_ => 0))),
        ("@sqlGoodsorderID", Join(rows.Select(r => r.GoodsOrderID))),
        ("@SqlStockpgHolInqt", Join(rows.Select(r => r.StockPgHolInqty))),
        ("@sqlIsNew", Join(rows.Select(r => r.IsNew ? 1 : 0))),
        ("@sqlSnManCode", Join(rows.Select(r => r.SnManCode))),
        ("@sqlPgManCode", Join(rows.Select(r => r.PgManCode))),
        ("@sqlKWManCode", Join(rows.Select(r => r.KwManCode))),
        ("@UsefulEndDate", Join(rows.Select(r => r.UsefulEndDate))),
    };

    foreach (var row in tempRows)
    {
        await Execute(conn, tx, """
            INSERT SaveTmptab(GUID, UName, UValues)
            VALUES(@Guid, @UName, @UValues)
            """,
            ("@Guid", guid),
            ("@UName", row.Name),
            ("@UValues", row.Value));
    }

    await using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = "p_hh_SaveCheckedCount";
    cmd.CommandType = CommandType.StoredProcedure;
    cmd.Parameters.AddWithValue("@SzGuid", guid);
    cmd.Parameters.AddWithValue("@szKtypeid", request.KTypeID);
    cmd.Parameters.AddWithValue("@szetypeid", request.ETypeID);
    cmd.Parameters.AddWithValue("@szDate", request.CheckDate);
    cmd.Parameters.AddWithValue("@checkedmode", 1);
    cmd.Parameters.AddWithValue("@updateTag", updateTag);
    var checkedId = new SqlParameter("@CheckedID", SqlDbType.Int) { Direction = ParameterDirection.Output };
    var returnValue = new SqlParameter("@ReturnValue", SqlDbType.Int) { Direction = ParameterDirection.ReturnValue };
    cmd.Parameters.Add(checkedId);
    cmd.Parameters.Add(returnValue);
    await cmd.ExecuteNonQueryAsync();

    var returnCode = returnValue.Value == DBNull.Value ? 0 : Convert.ToInt32(returnValue.Value, CultureInfo.InvariantCulture);
    if (returnCode < 0)
    {
        throw new InvalidOperationException($"ERP保存盘点失败，p_hh_SaveCheckedCount 返回 {returnCode}");
    }
}

static async Task<int> FindCheckedCountId(SqlConnection conn, SqlTransaction tx, SubmissionRequest request, int updateTag, PreparedCheckRow row)
{
    return await InsertScalarInt(conn, tx, """
        SELECT TOP 1 cc.ID
        FROM CheckedCount cc
        WHERE cc.KTypeID = @KTypeID
          AND cc.PTypeID = @PTypeID
          AND cc.ETypeid = @ETypeID
          AND cc.Date = @CheckDate
          AND cc.UpdateTag = @UpdateTag
          AND cc.CHECKEDMODE = 1
          AND cc.GoodsOrderID = @GoodsOrderID
          AND cc.CheckedNumber = @CheckedBaseQty
          AND cc.StockQty = @StockQty
          AND ISNULL(cc.JobNumber, '') = @JobNumber
          AND ISNULL(cc.OutFactoryDate, '') = @OutFactoryDate
          AND ISNULL(cc.UsefulEndDate, '') = @UsefulEndDate
          AND ISNULL(cc.GoodsBatchID, '') = @GoodsBatchID
          AND NOT EXISTS (SELECT 1 FROM CodexPdaCheckedCountMap m WHERE m.CheckedCountID = cc.ID AND m.UpdateTag = cc.UpdateTag)
        ORDER BY cc.ID DESC
        """,
        ("@KTypeID", request.KTypeID),
        ("@PTypeID", row.PTypeID),
        ("@ETypeID", request.ETypeID),
        ("@CheckDate", request.CheckDate),
        ("@UpdateTag", updateTag),
        ("@GoodsOrderID", row.GoodsOrderID),
        ("@CheckedBaseQty", row.CheckedBaseQty),
        ("@StockQty", row.StockQty),
        ("@JobNumber", row.JobNumber),
        ("@OutFactoryDate", row.OutFactoryDate),
        ("@UsefulEndDate", row.UsefulEndDate),
        ("@GoodsBatchID", row.GoodsBatchID));
}

static async Task<string> ExecuteScalarString(SqlConnection conn, SqlTransaction tx, string sql)
{
    await using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = sql;
    return Convert.ToString(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture) ?? "";
}

static string FormatSaveValue(object? value) => value switch
{
    null => "",
    decimal d => d.ToString("0.##########", CultureInfo.InvariantCulture),
    double d => d.ToString("0.##########", CultureInfo.InvariantCulture),
    float f => f.ToString("0.##########", CultureInfo.InvariantCulture),
    IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
    _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
};

static async Task<List<UnitDto>> LoadUnits(SqlConnection conn, string ptypeid, SqlTransaction? tx = null)
{
    await using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = """
        SELECT Ordid, Unit1, URate, IsBase
        FROM xw_PtypeUnit
        WHERE PTypeId = @PTypeID
        ORDER BY IsBase DESC, CAST(Ordid AS int)
        """;
    cmd.Parameters.AddWithValue("@PTypeID", ptypeid);

    var rows = new List<UnitDto>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        rows.Add(new UnitDto(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetDecimal(2),
            reader.GetInt32(3) == 1));
    }

    return rows.Count == 0 ? [new UnitDto("0", "", 1, true)] : rows;
}

static async Task<BatchIdentity> ResolveBatchIdentity(SqlConnection conn, SqlTransaction tx, string ptypeid, string ktypeid, SubmissionBatchRequest batch)
{
    var goodsBatchId = batch.GoodsBatchID ?? "";
    var goodsOrderId = batch.GoodsOrderID;
    if (!string.IsNullOrWhiteSpace(goodsBatchId) || string.IsNullOrWhiteSpace(batch.JobNumber))
    {
        return new BatchIdentity(goodsBatchId, goodsOrderId);
    }

    await using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = "p_hh_GetStockPtypecheckGoodsBatchID";
    cmd.CommandType = CommandType.StoredProcedure;
    cmd.Parameters.AddWithValue("@Cmode", "");
    cmd.Parameters.AddWithValue("@Ptypeid", ptypeid);
    cmd.Parameters.AddWithValue("@Ktypeid", ktypeid);
    cmd.Parameters.AddWithValue("@kwtypeid", "");
    cmd.Parameters.AddWithValue("@szBlockno", batch.JobNumber ?? "");
    cmd.Parameters.AddWithValue("@szProdate", batch.OutFactoryDate ?? "");
    cmd.Parameters.AddWithValue("@szUsefulEndDate", batch.UsefulEndDate ?? "");
    var orderParam = new SqlParameter("@goodsorderID", SqlDbType.Int) { Direction = ParameterDirection.Output, Value = goodsOrderId };
    var batchParam = new SqlParameter("@szGoodsBatchID", SqlDbType.VarChar, 50) { Direction = ParameterDirection.Output, Value = goodsBatchId };
    cmd.Parameters.Add(orderParam);
    cmd.Parameters.Add(batchParam);
    await cmd.ExecuteNonQueryAsync();

    return new BatchIdentity(
        Convert.ToString(batchParam.Value, CultureInfo.InvariantCulture) ?? goodsBatchId,
        orderParam.Value == DBNull.Value ? goodsOrderId : Convert.ToInt32(orderParam.Value, CultureInfo.InvariantCulture));
}

static async Task<List<StockRowDto>> LoadStockRows(SqlConnection conn, string ktypeid, string ptypeid, string checkDate, string etypeid)
{
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "P_HH_QueryOneGoodsStocks";
    cmd.CommandType = CommandType.StoredProcedure;
    cmd.Parameters.AddWithValue("@cMode", "");
    cmd.Parameters.AddWithValue("@szKTypeID", ktypeid);
    cmd.Parameters.AddWithValue("@szSLTypeID", "");
    cmd.Parameters.AddWithValue("@szKWTypeID", "");
    cmd.Parameters.AddWithValue("@szPTypeID", ptypeid);
    cmd.Parameters.AddWithValue("@GoodsBatchID", "");
    cmd.Parameters.AddWithValue("@GoodsorderID", 0);
    cmd.Parameters.AddWithValue("@qty", 0m);
    cmd.Parameters.AddWithValue("@Date", checkDate);
    cmd.Parameters.AddWithValue("@Operator", etypeid);

    var rows = new List<StockRowDto>();
    await using var reader = await cmd.ExecuteReaderAsync();
    do
    {
        if (!HasColumn(reader, "ptypeid") || !HasColumn(reader, "StockQty"))
        {
            continue;
        }

        while (await reader.ReadAsync())
        {
            rows.Add(new StockRowDto(
                reader["ptypeid"].ToString() ?? ptypeid,
                reader["sltypeid"].ToString() ?? "",
                ToInt(reader["prec"]),
                reader["pusercode"].ToString() ?? "",
                reader["pfullname"].ToString() ?? "",
                ToBool(reader["pgManCode"]),
                ToBool(reader["snManCode"]),
                ToDecimal(reader["StockQty"]),
                ToDecimal(reader["total"]),
                reader["JobNumber"].ToString() ?? "",
                reader["OutFactoryDate"].ToString() ?? "",
                ToInt(reader["costmode"]),
                ToInt(reader["GOODSORDERID"]),
                ToDecimal(reader["pgholInqty"]),
                reader["UsefulEndDate"].ToString() ?? "",
                reader["GoodsBatchID"].ToString() ?? "",
                ToBool(reader["IsNew"])));
        }
    }
    while (await reader.NextResultAsync());

    var hasBatchRows = rows.Any(r => !string.IsNullOrWhiteSpace(r.GoodsBatchID) || !string.IsNullOrWhiteSpace(r.JobNumber));
    if (!hasBatchRows)
    {
        var goodsStockBatchRows = await LoadGoodsStocksBatchRows(conn, ktypeid, ptypeid);
        if (goodsStockBatchRows.Count > 0)
        {
            return goodsStockBatchRows;
        }
    }

    return rows;
}

static async Task<List<StockRowDto>> LoadGoodsStocksBatchRows(SqlConnection conn, string ktypeid, string ptypeid)
{
    var pjobRows = await LoadPJobGoodsStocksBatchRows(conn, ktypeid, ptypeid);
    if (pjobRows.Count > 0)
    {
        return pjobRows;
    }

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        SELECT gs.PtypeId, '' AS sltypeid, p.prec, p.pusercode, p.pfullname,
               p.pgManCode, p.SNManCode, gs.Qty AS StockQty, gs.Total,
               gs.JobNumber, gs.OutFactoryDate, p.costmode,
               ISNULL(NULLIF(gs.GOODSORDERID, 0), gs.GoodsOrder) AS GOODSORDERID,
               gs.pgholInqty, gs.UsefulEndDate, gs.GoodsBatchID
        FROM GoodsStocks gs
        INNER JOIN ptype p ON p.ptypeid = gs.PtypeId
        WHERE gs.KtypeId = @KTypeID
          AND gs.PtypeId = @PTypeID
          AND (ISNULL(gs.GoodsBatchID, '') <> '' OR ISNULL(gs.JobNumber, '') <> '')
          AND (ISNULL(gs.Qty, 0) <> 0 OR ISNULL(gs.pgholInqty, 0) <> 0)
        ORDER BY gs.GoodsBatchID, gs.JobNumber, gs.OutFactoryDate, gs.GOODSORDERID
        """;
    cmd.Parameters.AddWithValue("@KTypeID", ktypeid);
    cmd.Parameters.AddWithValue("@PTypeID", ptypeid);

    var rows = new List<StockRowDto>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        rows.Add(new StockRowDto(
            reader["PtypeId"].ToString() ?? ptypeid,
            reader["sltypeid"].ToString() ?? "",
            ToInt(reader["prec"]),
            reader["pusercode"].ToString() ?? "",
            reader["pfullname"].ToString() ?? "",
            ToBool(reader["pgManCode"]),
            ToBool(reader["SNManCode"]),
            ToDecimal(reader["StockQty"]),
            ToDecimal(reader["Total"]),
            reader["JobNumber"].ToString() ?? "",
            reader["OutFactoryDate"].ToString() ?? "",
            ToInt(reader["costmode"]),
            ToInt(reader["GOODSORDERID"]),
            ToDecimal(reader["pgholInqty"]),
            reader["UsefulEndDate"].ToString() ?? "",
            reader["GoodsBatchID"].ToString() ?? "",
            false));
    }

    return rows;
}

static async Task<List<StockRowDto>> LoadPJobGoodsStocksBatchRows(SqlConnection conn, string ktypeid, string ptypeid)
{
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        SELECT gs.PtypeId, '' AS sltypeid, p.prec, p.pusercode, p.pfullname,
               p.pgManCode, p.SNManCode, gs.Qty AS StockQty, gs.Total,
               gs.JobNumber, gs.OutFactoryDate, p.costmode,
               gs.GOODSORDERID, gs.pgholInqty, gs.UsefulEndDate, gs.GoodsBatchID
        FROM PJobGoodsStocks gs
        INNER JOIN ptype p ON p.ptypeid = gs.PtypeId
        WHERE gs.KtypeId = @KTypeID
          AND gs.PtypeId = @PTypeID
          AND (ISNULL(gs.GoodsBatchID, '') <> '' OR ISNULL(gs.JobNumber, '') <> '')
          AND (ISNULL(gs.Qty, 0) <> 0 OR ISNULL(gs.pgholInqty, 0) <> 0)
        ORDER BY gs.JobNumber, gs.GoodsBatchID, gs.PJGOODSORDERID
        """;
    cmd.Parameters.AddWithValue("@KTypeID", ktypeid);
    cmd.Parameters.AddWithValue("@PTypeID", ptypeid);

    var rows = new List<StockRowDto>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        rows.Add(new StockRowDto(
            reader["PtypeId"].ToString() ?? ptypeid,
            reader["sltypeid"].ToString() ?? "",
            ToInt(reader["prec"]),
            reader["pusercode"].ToString() ?? "",
            reader["pfullname"].ToString() ?? "",
            ToBool(reader["pgManCode"]),
            ToBool(reader["SNManCode"]),
            ToDecimal(reader["StockQty"]),
            ToDecimal(reader["Total"]),
            reader["JobNumber"].ToString() ?? "",
            reader["OutFactoryDate"].ToString() ?? "",
            ToInt(reader["costmode"]),
            ToInt(reader["GOODSORDERID"]),
            ToDecimal(reader["pgholInqty"]),
            reader["UsefulEndDate"].ToString() ?? "",
            reader["GoodsBatchID"].ToString() ?? "",
            false));
    }

    return rows;
}

static async Task<WarehouseCheckState?> GetWarehouseCheck(SqlConnection conn, SqlTransaction tx, string ktypeid)
{
    await using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = "EXEC CodexPda_GetWarehouseCheck @KTypeID";
    cmd.Parameters.AddWithValue("@KTypeID", ktypeid);
    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
    {
        return null;
    }

    return new WarehouseCheckState(
        reader.GetString(reader.GetOrdinal("Date")),
        IsCheckEnded(reader.GetInt32(reader.GetOrdinal("CHECKEDMODE"))),
        HasColumn(reader, "UpdateTag") ? ToInt(reader["UpdateTag"]) : 0);
}

static bool IsCheckEnded(int checkedMode) => checkedMode == 0;

static async Task<int> InsertScalarInt(SqlConnection conn, SqlTransaction tx, string sql, params (string Name, object Value)[] parameters)
{
    await using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = sql;
    foreach (var p in parameters)
    {
        cmd.Parameters.AddWithValue(p.Name, p.Value);
    }

    return Convert.ToInt32(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
}

static async Task Execute(SqlConnection conn, SqlTransaction tx, string sql, params (string Name, object Value)[] parameters)
{
    await using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = sql;
    foreach (var p in parameters)
    {
        cmd.Parameters.AddWithValue(p.Name, p.Value);
    }

    await cmd.ExecuteNonQueryAsync();
}

static bool HasColumn(IDataRecord reader, string name)
{
    for (var i = 0; i < reader.FieldCount; i++)
    {
        if (string.Equals(reader.GetName(i), name, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
    }
    return false;
}

static decimal ToDecimal(object value) => value == DBNull.Value ? 0 : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
static int ToInt(object value) => value == DBNull.Value ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
static bool ToBool(object value) => ToInt(value) != 0;

public sealed class Db(IConfiguration configuration)
{
    private readonly string _connectionString = configuration.GetConnectionString("Default")
        ?? "Server=.;Database=hh2j1332;Trusted_Connection=True;Encrypt=False;TrustServerCertificate=True;";

    public async Task<SqlConnection> OpenAsync()
    {
        var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        return conn;
    }
}

record LoginRequest(string? Login, string? Password);
record OperatorDto(string ETypeID, string FullName, string UserCode);
record WarehouseDto(string KTypeID, string UserCode, string FullName, string Name);
record WarehouseCheckDto(bool Exists, bool Ended, string? CheckDate, string? WarehouseName, string? Message, int UpdateTag = 0);
record WarehouseCheckState(string Date, bool Ended, int UpdateTag);
record UnitDto(string Ordid, string Name, decimal Rate, bool IsBase);
record BatchIdentity(string GoodsBatchID, int GoodsOrderID);
record GoodsSaveInfo(int Prec, int CostMode, int PgManCode, int SnManCode, int KwManCode);
record PreparedCheckRow(int DetailID, string PTypeID, int Prec, int CostMode, int PgManCode, int SnManCode, int KwManCode, int GoodsOrderID, decimal CheckedBaseQty, decimal StockQty, decimal StockPgHolInqty, string JobNumber, string OutFactoryDate, string UsefulEndDate, string GoodsBatchID, bool IsNew);
record ProductCategoryDto(string PTypeID, string FullName);
record GoodsSearchDto(string PTypeID, int Prec, string UserCode, string FullName, string Name, int CostMode, bool PgManCode, bool SnManCode, bool KwManCode, bool PJobManCode, int UsefulLifeMonth, int UsefulLifeDay, string UnitText, string SideUnit, decimal SideUnitRate, bool HasBatch, List<UnitDto> Units);
record GoodsScanDto(GoodsSearchDto? Goods, List<StockRowDto> StockRows, long SearchElapsedMs, long StockElapsedMs, long TotalElapsedMs);
record GoodsListDto(GoodsSearchDto Goods, decimal StockQty, decimal StockPgHolInqty, int StockRowCount);
record StockRowDto(string PTypeID, string SLTypeID, int Prec, string UserCode, string FullName, bool PgManCode, bool SnManCode, decimal StockQty, decimal Total, string JobNumber, string OutFactoryDate, int CostMode, int GoodsOrderID, decimal StockPgHolInqty, string UsefulEndDate, string GoodsBatchID, bool IsNew);
record SubmissionRequest(string KTypeID, string CheckDate, string ETypeID, string? Remark, List<SubmissionItemRequest> Items);
record SubmissionItemRequest(string PTypeID, string UnitOrdid, decimal StockQty, decimal CountQty, List<SubmissionBatchRequest> Batches);
record SubmissionBatchRequest(string? GoodsBatchID, int GoodsOrderID, string? JobNumber, string? OutFactoryDate, string? UsefulEndDate, decimal StockQty, decimal StockPgHolInqty, decimal CountQty, decimal CountPgHolQty, bool IsNew, bool Deleted = false);
record HistoryDto(int SubmitID, string KTypeID, string WarehouseName, string CheckDate, string ETypeID, string OperatorName, DateTime SubmittedAt, int ItemCount, int BatchCount);
record HistoryDetailDto(int DetailID, string PTypeID, string UserCode, string FullName, string UnitOrdid, string UnitName, decimal UnitRate, string GoodsBatchID, int GoodsOrderID, string JobNumber, string OutFactoryDate, string UsefulEndDate, decimal StockQty, decimal CheckedQty, decimal CheckedBaseQty, decimal ProfitQty, bool IsNew);
