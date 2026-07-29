import React, { useEffect, useMemo, useRef, useState } from 'react'
import {
  Button,
  CalendarPicker,
  Card,
  Dialog,
  Dropdown,
  Empty,
  Form,
  Input,
  List,
  NavBar,
  NumberKeyboard,
  Popup,
  SearchBar,
  SideBar,
  Tag,
  Toast,
  VirtualInput,
} from 'antd-mobile'
import {
  CloseCircleOutline,
  DeleteOutline,
  AppOutline,
  LeftOutline,
  RedoOutline,
  ScanCodeOutline,
  UnorderedListOutline,
} from 'antd-mobile-icons'

const api = async (url, options = {}) => {
  const response = await fetch(url, {
    headers: { 'Content-Type': 'application/json', ...(options.headers || {}) },
    ...options,
  })
  if (response.status === 401) throw new Error('操作员或密码不正确')
  const contentType = response.headers.get('content-type') || ''
  const data = contentType.includes('application/json') ? await response.json() : null
  if (!response.ok) throw new Error(data?.message || data?.title || '请求失败')
  return data
}

const fmt = value => {
  const n = Number(value || 0)
  if (Number.isInteger(n)) return `${n}`
  return n.toFixed(4).replace(/0+$/, '').replace(/\.$/, '')
}

const baseUnit = units => units?.find(u => u.isBase) || units?.[0] || { ordid: '0', name: '基本单位', rate: 1 }
const unitName = unit => unit?.name || '基本单位'
const itemKey = ptypeid => `item-${ptypeid}`
const unitCountsBase = (unitCounts, units) =>
  Object.entries(unitCounts || {}).reduce((sum, [ordid, qty]) => {
    const unit = units.find(u => u.ordid === ordid)
    return sum + Number(qty || 0) * Number(unit?.rate || 1)
  }, 0)
const batchCountBase = (batch, units, fallbackUnitOrdid) => {
  if (Object.keys(batch.unitCounts || {}).length > 0) return unitCountsBase(batch.unitCounts, units)
  const unit = units.find(u => u.ordid === fallbackUnitOrdid) || baseUnit(units)
  return Number(batch.countQty || 0) * Number(unit?.rate || 1)
}
const formatDateValue = date => {
  if (!date) return ''
  const y = date.getFullYear()
  const m = `${date.getMonth() + 1}`.padStart(2, '0')
  const d = `${date.getDate()}`.padStart(2, '0')
  return `${y}-${m}-${d}`
}
const buildRelativeYearDate = years => {
  const date = new Date()
  date.setHours(0, 0, 0, 0)
  date.setFullYear(date.getFullYear() + years)
  return date
}
const calendarMinDate = buildRelativeYearDate(-5)
const calendarMaxDate = buildRelativeYearDate(5)
const parseDateValue = value => {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value || '')
  if (!match) return null
  const [, year, month, day] = match
  const [y, m, d] = [Number(year), Number(month), Number(day)]
  if (!y || !m || !d) return null
  const date = new Date(y, m - 1, d)
  return formatDateValue(date) === value ? date : null
}
const cleanDateInput = value => {
  const digits = (value || '').replace(/\D/g, '').slice(0, 8)
  if (digits.length <= 4) return digits
  if (digits.length <= 6) return `${digits.slice(0, 4)}-${digits.slice(4)}`
  return `${digits.slice(0, 4)}-${digits.slice(4, 6)}-${digits.slice(6)}`
}
const buildUsefulEndDate = (outFactoryDate, goods) => {
  const date = parseDateValue(outFactoryDate)
  if (!date) return ''
  const months = Number(goods?.usefulLifeMonth || 0)
  const days = Number(goods?.usefulLifeDay || 0)
  if (months === 0 && days === 0) return ''
  const end = new Date(date)
  if (months) end.setMonth(end.getMonth() + months)
  if (days) end.setDate(end.getDate() + days)
  return formatDateValue(end)
}
const batchTitleText = batch => batch.jobNumber || batch.goodsBatchID || '请输入批号'
const createItemFromStockRows = (goods, stockRows, forceBatch) => {
  const hasBatch = forceBatch || goods.hasBatch || stockRows.some(r => r.goodsBatchID || r.jobNumber)
  const batches = hasBatch
    ? stockRows
        .filter(r => r.goodsBatchID || r.jobNumber || (goods.hasBatch && (Number(r.stockQty || 0) !== 0 || Number(r.stockPgHolInqty || 0) !== 0)))
        .map(r => ({
          goodsBatchID: r.goodsBatchID || '',
          goodsOrderID: r.goodsOrderID || 0,
          jobNumber: r.jobNumber || '',
          outFactoryDate: r.outFactoryDate || '',
          usefulEndDate: r.usefulEndDate || '',
          stockQty: Number(r.stockQty || 0),
          stockPgHolInqty: Number(r.stockPgHolInqty || 0),
          countQty: 0,
          countPgHolQty: 0,
          unitCounts: {},
          isNew: Boolean(r.isNew),
          deleted: false,
        }))
    : []
  return {
    goods,
    units: goods.units || [],
    unitOrdid: baseUnit(goods.units).ordid,
    stockQty: stockRows.reduce((sum, r) => sum + Number(r.stockQty || 0), 0),
    countQty: 0,
    unitCounts: {},
    isBatchManaged: hasBatch,
    batches,
  }
}

function QuantityStepper({ value, onChange, min = 0 }) {
  const [draft, setDraft] = useState(fmt(value))
  const [editing, setEditing] = useState(false)
  const rootRef = useRef(null)
  const replaceOnNextInputRef = useRef(false)

  useEffect(() => {
    if (!editing) setDraft(fmt(value))
  }, [value, editing])

  const commit = raw => {
    const parsed = Number(String(raw || '').replace(',', '.'))
    const next = Number.isFinite(parsed) ? Math.max(min, parsed) : min
    setDraft(fmt(next))
    onChange(next)
  }
  const step = delta => {
    const current = Number(value || 0)
    const next = Math.max(min, current + delta)
    setDraft(fmt(next))
    onChange(next)
  }

  return (
    <div className="quantity-stepper" ref={rootRef}>
      <button type="button" className="quantity-stepper-button" onClick={() => step(-1)} disabled={Number(value || 0) <= min}>-</button>
      <VirtualInput
        className="quantity-stepper-input"
        value={draft}
        keyboard={<NumberKeyboard customKey="." confirmText="确定" title={'数量：' + (draft || '0')} />}
        onFocus={() => {
          setEditing(true)
          setDraft(fmt(value))
          replaceOnNextInputRef.current = true
          window.setTimeout(() => {
            rootRef.current?.scrollIntoView({ block: 'center', behavior: 'smooth' })
          }, 80)
        }}
        onChange={value => {
          const raw = replaceOnNextInputRef.current ? value.slice(-1) : value
          replaceOnNextInputRef.current = false
          const next = raw.replace(/[^\d.]/g, '').replace(/^(\d*\.\d*).*$/, '$1').replace(/^\./, '0.')
          setDraft(next)
          const parsed = Number(next)
          if (next !== '' && Number.isFinite(parsed)) onChange(Math.max(min, parsed))
        }}
        onBlur={() => {
          setEditing(false)
          replaceOnNextInputRef.current = false
          commit(draft)
        }}
      />
      <button type="button" className="quantity-stepper-button quantity-stepper-plus" onClick={() => step(1)}>+</button>
    </div>
  )
}

function App() {
  const [operator, setOperator] = useState(null)
  const [page, setPage] = useState('home')
  const [warehouses, setWarehouses] = useState([])
  const [check, setCheck] = useState(null)
  const [items, setItems] = useState([])
  const [categories, setCategories] = useState([])
  const [category, setCategory] = useState('00000')
  const [products, setProducts] = useState([])
  const [selectKeyword, setSelectKeyword] = useState('')
  const [scanKeyword, setScanKeyword] = useState('')
  const [editingIndex, setEditingIndex] = useState(-1)
  const [editingBatches, setEditingBatches] = useState([])
  const [batchPopupVisible, setBatchPopupVisible] = useState(false)
  const [newBatch, setNewBatch] = useState({ goodsBatchID: '', outFactoryDate: '', usefulEndDate: '', unitCounts: {} })
  const [calendarField, setCalendarField] = useState(null)
  const [calendarValue, setCalendarValue] = useState(null)
  const [history, setHistory] = useState([])
  const [scannerVisible, setScannerVisible] = useState(false)
  const [scannerTarget, setScannerTarget] = useState('check')
  const [scannerMsg, setScannerMsg] = useState('')
  const [lastSelectedPTypeID, setLastSelectedPTypeID] = useState(null)
  const [scanFlash, setScanFlash] = useState({ pTypeID: null, token: 0 })
  const [form] = Form.useForm()
  const videoRef = useRef(null)
  const streamRef = useRef(null)
  const lastScanRef = useRef({ value: '', at: 0 })
  const hasDraftItems = items.length > 0

  useEffect(() => {
    const saved = localStorage.getItem('codexPdaOperator')
    if (saved) {
      setOperator(JSON.parse(saved))
      loadWarehouses()
    }
  }, [])

  useEffect(() => {
    if (page === 'check' && lastSelectedPTypeID) {
      requestAnimationFrame(() => {
        document.getElementById(itemKey(lastSelectedPTypeID))?.scrollIntoView({ block: 'center', behavior: 'smooth' })
      })
    }
  }, [page, lastSelectedPTypeID, items.length])

  useEffect(() => {
    if (page === 'select' && check?.ktypeid) loadProducts()
  }, [page, category])

  const loadWarehouses = async () => {
    setWarehouses(await api('/api/warehouses'))
  }

  const doLogin = async values => {
    try {
      const user = await api('/api/login', {
        method: 'POST',
        body: JSON.stringify({ login: values.login, password: values.password }),
      })
      setOperator(user)
      localStorage.setItem('codexPdaOperator', JSON.stringify(user))
      await loadWarehouses()
    } catch (err) {
      Toast.show({ icon: 'fail', content: err.message })
    }
  }

  const logout = () => {
    localStorage.removeItem('codexPdaOperator')
    setOperator(null)
    setCheck(null)
    setItems([])
    setPage('home')
  }

  const loadWarehouse = async ktypeid => {
    form.setFieldsValue({ warehouse: ktypeid })
    setItems([])
    setScanKeyword('')
    if (!ktypeid) {
      setCheck(null)
      form.setFieldsValue({ checkDate: '' })
      return
    }
    const data = await api(`/api/check-session/${encodeURIComponent(ktypeid)}`)
    setCheck({ ...data, ktypeid })
    if (!data.exists || data.ended) {
      form.setFieldsValue({ checkDate: '' })
      Toast.show({ icon: 'fail', content: data.message || '该仓库没有可用盘点单' })
      return
    }
    form.setFieldsValue({ checkDate: data.checkDate })
  }

  const confirmLeaveDraft = async nextAction => {
    if (!hasDraftItems) {
      await nextAction()
      return
    }
    const shouldLeave = await Dialog.confirm({
      title: '当前开单有数据',
      content: '离开后未保存数据将丢失，是否不保存并离开？',
      confirmText: '不保存',
      cancelText: '取消',
    })
    if (!shouldLeave) return
    setItems([])
    setLastSelectedPTypeID(null)
    await nextAction()
  }

  const chooseWarehouse = async ktypeid => {
    if (hasDraftItems && check?.ktypeid !== ktypeid) {
      await confirmLeaveDraft(() => loadWarehouse(ktypeid))
      return
    }
    await loadWarehouse(ktypeid)
  }

  const backHomeFromCheck = async () => {
    await confirmLeaveDraft(async () => {
      setPage('home')
    })
  }

  const requireReady = () => {
    if (!check?.ktypeid || !check?.checkDate || check.ended) {
      Toast.show({ icon: 'fail', content: '请先选择一个未结束的盘点仓库' })
      setPage('check')
      return false
    }
    return true
  }

  const openSelect = async () => {
    if (!requireReady()) return
    setPage('select')
    if (categories.length === 0) {
      const rows = await api('/api/goods/categories')
      setCategories(rows)
    }
    await loadProducts()
  }

  const loadProducts = async (keyword = selectKeyword, categoryOverride = category) => {
    if (!check?.ktypeid || !operator?.eTypeID) return
    const url = `/api/goods/list?categoryId=${encodeURIComponent(categoryOverride)}&q=${encodeURIComponent(keyword || '')}&ktypeid=${encodeURIComponent(check.ktypeid)}&date=${encodeURIComponent(check.checkDate)}&etypeid=${encodeURIComponent(operator.eTypeID)}`
    setProducts(await api(url))
  }

  const searchScan = async keyword => {
    const q = keyword.trim()
    if (!q || !check?.ktypeid) return false

    const scan = await api(`/api/goods/scan?q=${encodeURIComponent(q)}&ktypeid=${encodeURIComponent(check.ktypeid)}&date=${encodeURIComponent(check.checkDate)}&etypeid=${encodeURIComponent(operator?.eTypeID || '00000')}`)
    if (scan.goods) {
      await addScannedGoods(scan.goods, scan.stockRows || [])
      return true
    }

    Toast.show('未找到匹配的条码')
    return false
  }

  const ensureItem = async (goods, forceBatch) => {
    const existing = items.find(i => i.goods.pTypeID === goods.pTypeID)
    if (existing) return existing
    const stockRows = await api(`/api/goods/${encodeURIComponent(goods.pTypeID)}/stock?ktypeid=${encodeURIComponent(check.ktypeid)}&date=${encodeURIComponent(check.checkDate)}&etypeid=${encodeURIComponent(operator.eTypeID)}`)
    const item = createItemFromStockRows(goods, stockRows, forceBatch)
    setItems(prev => [item, ...prev])
    return item
  }

  const addScannedGoods = async (goods, stockRows) => {
    if (!requireReady()) return
    const existing = items.find(i => i.goods.pTypeID === goods.pTypeID)
    const item = existing || createItemFromStockRows(goods, stockRows, goods.hasBatch)
    if (!existing) setItems(prev => [item, ...prev])
    if (item.isBatchManaged || item.batches.length > 0) {
      const index = items.findIndex(i => i.goods.pTypeID === goods.pTypeID)
      openBatch(index >= 0 ? index : 0, item)
      return
    }
    const unit = baseUnit(item.units)
    incrementItemUnitCount(item.goods.pTypeID, unit.ordid, 1)
    setLastSelectedPTypeID(item.goods.pTypeID)
    setScanFlash({ pTypeID: item.goods.pTypeID, token: Date.now() })
    setPage('check')
  }

  const addGoods = async (goods, mode) => {
    if (!requireReady()) return
    const item = await ensureItem(goods, mode === 'batch')
    if (mode === 'batch' || item.isBatchManaged || item.batches.length > 0) {
      const index = items.findIndex(i => i.goods.pTypeID === goods.pTypeID)
      const actualIndex = index >= 0 ? index : 0
      openBatch(actualIndex, items[actualIndex] || item)
      return
    }
    const unit = baseUnit(item.units)
    incrementItemUnitCount(item.goods.pTypeID, unit.ordid, 1)
    setLastSelectedPTypeID(item.goods.pTypeID)
    setPage('check')
  }

  const setGoodsUnitCount = async (goods, unit, value) => {
    if (!requireReady()) return
    await ensureItem(goods, false)
    setItems(prev =>
      prev.map(item => {
        if (item.goods.pTypeID !== goods.pTypeID) return item
        return { ...item, unitCounts: { ...(item.unitCounts || {}), [unit.ordid]: Number(value || 0) } }
      })
    )
    setLastSelectedPTypeID(goods.pTypeID)
  }

  const updateItemUnitCount = (ptypeid, ordid, value) => {
    setItems(prev =>
      prev.map(item => {
        if (item.goods.pTypeID !== ptypeid) return item
        return { ...item, unitCounts: { ...(item.unitCounts || {}), [ordid]: Number(value || 0) } }
      })
    )
  }

  const incrementItemUnitCount = (ptypeid, ordid, delta = 1) => {
    setItems(prev =>
      prev.map(item => {
        if (item.goods.pTypeID !== ptypeid) return item
        const current = Number(item.unitCounts?.[ordid] || 0)
        return { ...item, unitCounts: { ...(item.unitCounts || {}), [ordid]: current + delta } }
      })
    )
  }

  const openBatch = (index, itemArg) => {
    const item = itemArg || items[index]
    if (!item) return
    setEditingIndex(index)
    setEditingBatches(item.batches.map(b => ({ ...b })))
    setPage('batch')
  }

  const updateBatchUnitCount = (batchIndex, ordid, value) => {
    setEditingBatches(prev =>
      prev.map((b, i) =>
        i === batchIndex
          ? { ...b, unitCounts: { ...(b.unitCounts || {}), [ordid]: Number(value || 0) } }
          : b
      )
    )
  }

  const confirmBatch = () => {
    const item = items[editingIndex]
    if (!item) return
    setItems(prev => prev.map((row, i) => (i === editingIndex ? { ...row, batches: editingBatches } : row)))
    setLastSelectedPTypeID(item.goods.pTypeID)
    setPage('check')
  }

  const updateNewBatchUnitCount = (ordid, value) => {
    setNewBatch(prev => ({ ...prev, unitCounts: { ...(prev.unitCounts || {}), [ordid]: Number(value || 0) } }))
  }

  const updateNewBatchDate = (field, value) => {
    const nextValue = cleanDateInput(value)
    setNewBatch(prev => {
      if (field !== 'outFactoryDate') return { ...prev, [field]: nextValue }
      const usefulEndDate = buildUsefulEndDate(nextValue, items[editingIndex]?.goods)
      return { ...prev, outFactoryDate: nextValue, usefulEndDate: usefulEndDate || prev.usefulEndDate }
    })
  }

  const openCalendar = field => {
    setCalendarValue(parseDateValue(newBatch[field]) || new Date())
    setCalendarField(field)
  }

  const closeCalendar = () => {
    setCalendarField(null)
    setCalendarValue(null)
  }

  const confirmCalendarDate = date => {
    const picked = date || calendarValue
    if (calendarField && picked) {
      const value = formatDateValue(picked)
      setNewBatch(prev => {
        if (calendarField !== 'outFactoryDate') return { ...prev, [calendarField]: value }
        return { ...prev, outFactoryDate: value, usefulEndDate: buildUsefulEndDate(value, items[editingIndex]?.goods) || prev.usefulEndDate }
      })
    }
    closeCalendar()
  }

  const addBatch = () => {
    const item = items[editingIndex]
    const unitCounts = { ...(newBatch.unitCounts || {}) }
    const inputBatchNo = newBatch.goodsBatchID.trim()
    const isJobBatch = Boolean(item?.goods?.pJobManCode)
    setEditingBatches(prev => [
      ...prev,
      {
        goodsBatchID: isJobBatch ? '' : inputBatchNo,
        goodsOrderID: 0,
        jobNumber: isJobBatch ? inputBatchNo : '',
        outFactoryDate: newBatch.outFactoryDate,
        usefulEndDate: newBatch.usefulEndDate,
        stockQty: 0,
        stockPgHolInqty: 0,
        countQty: item ? unitCountsBase(unitCounts, item.units) : 0,
        countPgHolQty: 0,
        unitCounts,
        isNew: true,
        deleted: false,
      },
    ])
    setNewBatch({ goodsBatchID: '', outFactoryDate: '', usefulEndDate: '', unitCounts: {} })
    setBatchPopupVisible(false)
  }

  const itemCheckedBase = item => {
    if (item.batches.length) {
      return item.batches.filter(b => !b.deleted).reduce((sum, b) => sum + batchCountBase(b, item.units, item.unitOrdid), 0)
    }
    return unitCountsBase(item.unitCounts, item.units)
  }

  const itemStock = item => {
    const activeBatches = item.batches.filter(b => !b.deleted)
    return activeBatches.length ? activeBatches.reduce((sum, b) => sum + Number(b.stockQty || 0), 0) : Number(item.stockQty || 0)
  }

  const totalProfit = useMemo(() => items.reduce((sum, item) => sum + itemCheckedBase(item) - itemStock(item), 0), [items])

  const submit = async ({ clearAfterSubmit = true } = {}) => {
    if (!requireReady()) return false
    if (items.length === 0) {
      Toast.show('没有可保存的盘点商品')
      return false
    }
    if (items.every(item => itemCheckedBase(item) === 0)) {
      Toast.show('没有填写盘点数量')
      return false
    }
    try {
      const payload = {
        kTypeID: check.ktypeid,
        checkDate: check.checkDate,
        eTypeID: operator.eTypeID,
        remark: 'PDA保存',
        items: items.map(item => ({
          pTypeID: item.goods.pTypeID,
          unitOrdid: baseUnit(item.units).ordid,
          stockQty: item.stockQty,
          countQty: itemCheckedBase(item),
          batches: item.batches.map(batch => ({
            ...batch,
            countQty: batchCountBase(batch, item.units, item.unitOrdid),
          })),
        })),
      }
      const result = await api('/api/submissions', { method: 'POST', body: JSON.stringify(payload) })
      Toast.show({ icon: 'success', content: `保存成功：${result.detailCount} 条` })
      if (clearAfterSubmit) {
        setItems([])
        setLastSelectedPTypeID(null)
      }
      return true
    } catch (err) {
      Toast.show({ icon: 'fail', content: err.message })
      return false
    }
  }

  const loadHistory = async () => {
    setPage('history')
    const query = check?.ktypeid ? `?ktypeid=${encodeURIComponent(check.ktypeid)}` : ''
    setHistory(await api(`/api/history${query}`))
  }

  const buildItemsFromHistory = details => {
    const groups = new Map()
    details.forEach(detail => {
      if (!groups.has(detail.pTypeID)) {
        groups.set(detail.pTypeID, [])
      }
      groups.get(detail.pTypeID).push(detail)
    })

    return Array.from(groups.values()).map(group => {
      const first = group[0]
      const unitMap = new Map()
      group.forEach(detail => {
        if (!unitMap.has(detail.unitOrdid)) {
          unitMap.set(detail.unitOrdid, {
            ordid: detail.unitOrdid,
            name: detail.unitName || '基本单位',
            rate: Number(detail.unitRate || 1),
            isBase: detail.unitOrdid === '0' || Number(detail.unitRate || 1) === 1,
          })
        }
      })
      const units = Array.from(unitMap.values()).sort((a, b) => Number(a.ordid) - Number(b.ordid))
      const hasBatch = group.some(detail => detail.goodsBatchID || detail.jobNumber || detail.outFactoryDate || detail.usefulEndDate || detail.isNew)
      return {
        goods: {
          pTypeID: first.pTypeID,
          userCode: first.userCode,
          fullName: first.fullName,
          name: first.fullName,
          unitText: units.map(unit => unitName(unit)).join(' / '),
          hasBatch,
          pJobManCode: group.some(detail => detail.jobNumber),
          units,
        },
        units,
        unitOrdid: baseUnit(units).ordid,
        stockQty: group.reduce((sum, detail) => sum + Number(detail.stockQty || 0), 0),
        countQty: group.reduce((sum, detail) => sum + Number(detail.checkedBaseQty || 0), 0),
        unitCounts: hasBatch ? {} : { [baseUnit(units).ordid]: Number(first.checkedBaseQty || first.checkedQty || 0) },
        isBatchManaged: hasBatch,
        batches: hasBatch
          ? group.map(detail => ({
              goodsBatchID: detail.goodsBatchID || '',
              goodsOrderID: detail.goodsOrderID || 0,
              jobNumber: detail.jobNumber || '',
              outFactoryDate: detail.outFactoryDate || '',
              usefulEndDate: detail.usefulEndDate || '',
              stockQty: Number(detail.stockQty || 0),
              stockPgHolInqty: 0,
              countQty: Number(detail.checkedBaseQty || detail.checkedQty || 0),
              countPgHolQty: 0,
              unitCounts: { [baseUnit(units).ordid]: Number(detail.checkedBaseQty || detail.checkedQty || 0) },
              isNew: Boolean(detail.isNew),
              deleted: false,
            }))
          : [],
      }
    })
  }

  const stockRowsToBatches = (stockRows, goods) => {
    const hasBatch = goods.hasBatch || stockRows.some(r => r.goodsBatchID || r.jobNumber)
    if (!hasBatch) return []
    return stockRows
      .filter(r => r.goodsBatchID || r.jobNumber || (goods.hasBatch && (Number(r.stockQty || 0) !== 0 || Number(r.stockPgHolInqty || 0) !== 0)))
      .map(r => ({
        goodsBatchID: r.goodsBatchID || '',
        goodsOrderID: r.goodsOrderID || 0,
        jobNumber: r.jobNumber || '',
        outFactoryDate: r.outFactoryDate || '',
        usefulEndDate: r.usefulEndDate || '',
        stockQty: Number(r.stockQty || 0),
        stockPgHolInqty: Number(r.stockPgHolInqty || 0),
        countQty: 0,
        countPgHolQty: 0,
        unitCounts: {},
        isNew: Boolean(r.isNew),
        deleted: false,
      }))
  }

  const sameBatch = (left, right) => {
    if (left.goodsBatchID && right.goodsBatchID && left.goodsBatchID === right.goodsBatchID) return true
    if (left.jobNumber && right.jobNumber) {
      if (left.jobNumber !== right.jobNumber) return false
      const sameDates =
        (left.outFactoryDate || '') === (right.outFactoryDate || '') &&
        (left.usefulEndDate || '') === (right.usefulEndDate || '')
      return sameDates || (!left.outFactoryDate && !right.outFactoryDate)
    }
    return false
  }

  const buildItemsFromHistoryForLatestCheck = async (details, targetCheck) => {
    const historyItems = buildItemsFromHistory(details)
    const nextItems = []

    for (const historyItem of historyItems) {
      const goodsRows = await api(`/api/goods/search?q=${encodeURIComponent(historyItem.goods.pTypeID)}&ktypeid=${encodeURIComponent(targetCheck.ktypeid)}`)
      const currentGoods = goodsRows.find(row => row.pTypeID === historyItem.goods.pTypeID) || historyItem.goods
      const goods = { ...historyItem.goods, ...currentGoods, units: currentGoods.units?.length ? currentGoods.units : historyItem.units }
      const units = goods.units || historyItem.units
      const stockRows = await api(`/api/goods/${encodeURIComponent(goods.pTypeID)}/stock?ktypeid=${encodeURIComponent(targetCheck.ktypeid)}&date=${encodeURIComponent(targetCheck.checkDate)}&etypeid=${encodeURIComponent(operator.eTypeID)}`)
      const stockQty = stockRows.reduce((sum, row) => sum + Number(row.stockQty || 0), 0)
      const isBatchManaged = goods.hasBatch || historyItem.isBatchManaged || stockRows.some(row => row.goodsBatchID || row.jobNumber)

      if (!isBatchManaged) {
        nextItems.push({
          ...historyItem,
          goods,
          units,
          unitOrdid: baseUnit(units).ordid,
          stockQty,
          isBatchManaged: false,
          batches: [],
        })
        continue
      }

      const latestBatches = stockRowsToBatches(stockRows, goods)
      const historyBatches = historyItem.batches.filter(batch => !batch.deleted)
      const usedHistoryIndexes = new Set()
      const mergedBatches = latestBatches.map(batch => {
        const matchIndex = historyBatches.findIndex((historyBatch, index) => !usedHistoryIndexes.has(index) && sameBatch(batch, historyBatch))
        if (matchIndex < 0) return batch
        usedHistoryIndexes.add(matchIndex)
        const historyBatch = historyBatches[matchIndex]
        return {
          ...batch,
          countQty: historyBatch.countQty,
          countPgHolQty: historyBatch.countPgHolQty,
          unitCounts: historyBatch.unitCounts || {},
        }
      })

      historyBatches.forEach((historyBatch, index) => {
        if (usedHistoryIndexes.has(index)) return
        mergedBatches.push({
          ...historyBatch,
          goodsOrderID: 0,
          stockQty: 0,
          stockPgHolInqty: 0,
          isNew: true,
          deleted: false,
        })
      })

      nextItems.push({
        ...historyItem,
        goods,
        units,
        unitOrdid: baseUnit(units).ordid,
        stockQty,
        unitCounts: {},
        isBatchManaged: true,
        batches: mergedBatches,
      })
    }

    return nextItems
  }

  const loadHistoryToCheck = async row => {
    if (hasDraftItems) {
      const overwrite = await Dialog.confirm({
        title: '开单界面已有数据',
        content: '是否覆盖当前开单数据？',
        confirmText: '覆盖',
        cancelText: '取消',
      })
      if (!overwrite) return
    }

    const state = await api(`/api/check-session/${encodeURIComponent(row.kTypeID)}`)
    if (!state.exists || state.ended || !state.checkDate) {
      Toast.show({ icon: 'fail', content: state.message || '该仓库没有可继续盘点的盘点单' })
      return
    }
    const details = await api(`/api/history/${row.submitID}`)
    const targetCheck = { ...state, ktypeid: row.kTypeID, checkDate: state.checkDate }
    const nextItems = await buildItemsFromHistoryForLatestCheck(details, targetCheck)
    setCheck(targetCheck)
    form.setFieldsValue({ warehouse: row.kTypeID, checkDate: targetCheck.checkDate })
    setItems(nextItems)
    setLastSelectedPTypeID(details[0]?.pTypeID || null)
    setPage('check')
  }

  const deleteHistory = async row => {
    try {
      const confirmed = await Dialog.confirm({
        title: '删除历史记录',
        content: '仅删除本次 PDA 提交历史，不会删除 ERP 盘点数据，是否继续？',
        confirmText: '删除',
        cancelText: '取消',
      })
      if (!confirmed) return
      const result = await api(`/api/history/${row.submitID}`, { method: 'DELETE' })
      Toast.show({ icon: result.deleted ? 'success' : 'fail', content: result.deleted ? '已删除' : '记录不存在或已删除' })
      await loadHistory()
    } catch (err) {
      Toast.show({ icon: 'fail', content: err.message || '删除失败' })
    }
  }

  const handleScannedValue = async (value, target) => {
    const scanned = String(value || '').trim()
    if (!scanned) return
    const now = Date.now()
    if (lastScanRef.current.value === scanned && now - lastScanRef.current.at < 120) return
    lastScanRef.current = { value: scanned, at: now }

    if (target === 'select') {
      setSelectKeyword(scanned)
      setPage('select')
      await loadProducts(scanned)
      return
    }
    const ok = await searchScan(scanned)
    setScanKeyword(ok ? '' : scanned)
  }

  useEffect(() => {
    window.__yodexHardwareScanResult = value => {
      const target = page === 'select' ? 'select' : 'check'
      handleScannedValue(value || '', target)
    }
    return () => {
      if (window.__yodexHardwareScanResult) delete window.__yodexHardwareScanResult
    }
  }, [page, check?.ktypeid, check?.checkDate, check?.ended, items.length, categories.length, selectKeyword])

  const openScanner = async target => {
    if (!requireReady()) return
    if (window.YodexNative?.scanCode) {
      window.__yodexNativeScanResult = value => {
        handleScannedValue(value || '', target)
      }
      window.YodexNative.scanCode()
      return
    }
    setScannerTarget(target)
    setScannerVisible(true)
    setScannerMsg('正在打开摄像头...')
    if (!navigator.mediaDevices?.getUserMedia) {
      setScannerMsg('浏览器摄像头扫码需要 HTTPS 或使用 APK，请使用扫码枪或手动输入')
      return
    }
    if (!('BarcodeDetector' in window)) {
      setScannerMsg('当前浏览器不支持条码识别，请使用支持 BarcodeDetector 的浏览器或扫码枪')
      return
    }
    try {
      const stream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: { ideal: 'environment' } }, audio: false })
      streamRef.current = stream
      const video = videoRef.current
      video.srcObject = stream
      await video.play()
      setScannerMsg('请将条码或二维码放入框内')
      const detector = new BarcodeDetector({ formats: ['qr_code', 'code_128', 'code_39', 'ean_13', 'ean_8', 'upc_a', 'upc_e', 'itf'] })
      scanLoop(detector, video, target)
    } catch (err) {
      setScannerMsg(`无法打开摄像头：${err.message}`)
    }
  }

  const closeScanner = () => {
    streamRef.current?.getTracks().forEach(track => track.stop())
    streamRef.current = null
    if (videoRef.current) videoRef.current.srcObject = null
    setScannerVisible(false)
  }

  useEffect(() => {
    window.__yodexHandleBack = () => {
      if (scannerVisible) {
        closeScanner()
        return true
      }
      if (page === 'home') return false
      if (page === 'check') {
        backHomeFromCheck()
        return true
      }
      if (page === 'batch') {
        setPage('select')
        return true
      }
      setPage('check')
      return true
    }
    return () => {
      if (window.__yodexHandleBack) delete window.__yodexHandleBack
    }
  }, [page, scannerVisible, hasDraftItems, check?.ktypeid, items.length])

  const scanLoop = async (detector, video, target) => {
    if (!streamRef.current) return
    try {
      const codes = await detector.detect(video)
      if (codes.length > 0) {
        const value = codes[0].rawValue || ''
        closeScanner()
        await handleScannedValue(value, target)
        return
      }
    } catch {
      // Ignore transient detection errors while the camera is focusing.
    }
    requestAnimationFrame(() => scanLoop(detector, video, target))
  }

  if (!operator) return <LoginPage onLogin={doLogin} />
  const popupBatchItem = items[editingIndex]

  return (
    <div className="adm-app">
      {page === 'home' && (
        <HomePage
          operator={operator}
          openCheck={() => setPage('check')}
          logout={logout}
        />
      )}
      {page === 'check' && (
        <CheckPage
          form={form}
          warehouses={warehouses}
          check={check}
          items={items}
          totalProfit={totalProfit}
          scanKeyword={scanKeyword}
          setScanKeyword={setScanKeyword}
          chooseWarehouse={chooseWarehouse}
          searchScan={searchScan}
          openScanner={() => openScanner('check')}
          openSelect={openSelect}
          openHistory={loadHistory}
          backHome={backHomeFromCheck}
          updateItemUnitCount={updateItemUnitCount}
          openBatch={openBatch}
          removeItem={index => setItems(prev => prev.filter((_, i) => i !== index))}
          itemCheckedBase={itemCheckedBase}
          itemStock={itemStock}
          scanFlash={scanFlash}
          submit={submit}
        />
      )}
      {page === 'select' && (
        <SelectPage
          categories={categories}
          category={category}
          setCategory={setCategory}
          products={products}
          keyword={selectKeyword}
          setKeyword={setSelectKeyword}
          searchProducts={loadProducts}
          items={items}
          setPage={setPage}
          addGoods={addGoods}
          setGoodsUnitCount={setGoodsUnitCount}
          openScanner={() => openScanner('select')}
        />
      )}
      {page === 'batch' && (
        <BatchPage
          item={items[editingIndex]}
          batches={editingBatches}
          setPage={setPage}
          updateBatchUnitCount={updateBatchUnitCount}
          setEditingBatches={setEditingBatches}
          confirmBatch={confirmBatch}
          setBatchPopupVisible={setBatchPopupVisible}
        />
      )}
      {page === 'history' && (
        <HistoryPage
          rows={history}
          setPage={setPage}
          refresh={loadHistory}
          loadHistoryToCheck={loadHistoryToCheck}
          deleteHistory={deleteHistory}
        />
      )}
      <Popup visible={batchPopupVisible} onMaskClick={() => setBatchPopupVisible(false)} position="bottom" bodyClassName="batch-popup">
        <Form layout="horizontal" footer={<Button block color="primary" size="large" onClick={addBatch}>确定</Button>}>
          <Form.Header>批号录入</Form.Header>
          <Form.Item label="批号">
            <Input placeholder="请输入" value={newBatch.goodsBatchID} onChange={value => setNewBatch(prev => ({ ...prev, goodsBatchID: value }))} />
          </Form.Item>
          <Form.Item label="生产日期">
            <div className="date-input-row">
              <Input
                placeholder="yyyy-MM-dd"
                inputMode="numeric"
                value={newBatch.outFactoryDate}
                onChange={value => updateNewBatchDate('outFactoryDate', value)}
              />
              <Button className="date-picker-trigger" fill="outline" color="primary" onClick={() => openCalendar('outFactoryDate')}>
                选择
              </Button>
            </div>
          </Form.Item>
          <Form.Item label="到期日期">
            <div className="date-input-row">
              <Input
                placeholder="yyyy-MM-dd"
                inputMode="numeric"
                value={newBatch.usefulEndDate}
                onChange={value => updateNewBatchDate('usefulEndDate', value)}
              />
              <Button className="date-picker-trigger" fill="outline" color="primary" onClick={() => openCalendar('usefulEndDate')}>
                选择
              </Button>
            </div>
          </Form.Item>
          <Form.Item label={<span className="danger-text">数量</span>}>
            <div className="unit-stack popup-unit-list">
              {popupBatchItem?.units.map(unit => (
                <div className="unit-row" key={unit.ordid}>
                  <span>{unitName(unit)}</span>
                  <QuantityStepper min={0} value={Number(newBatch.unitCounts?.[unit.ordid] || 0)} onChange={value => updateNewBatchUnitCount(unit.ordid, value)} />
                </div>
              ))}
            </div>
          </Form.Item>
        </Form>
      </Popup>
      <CalendarPicker
        visible={Boolean(calendarField)}
        selectionMode="single"
        title={calendarField === 'outFactoryDate' ? '选择生产日期' : '选择到期日期'}
        value={calendarValue}
        onChange={setCalendarValue}
        min={calendarMinDate}
        max={calendarMaxDate}
        confirmText="确定"
        closeOnMaskClick
        popupClassName="date-calendar-popup"
        popupStyle={{ zIndex: 1300 }}
        onClose={closeCalendar}
        onConfirm={confirmCalendarDate}
      />
      <Popup visible={scannerVisible} position="right" bodyClassName="scanner-popup" destroyOnClose>
        <NavBar onBack={closeScanner}>扫码</NavBar>
        <div className="scanner-stage">
          <video ref={videoRef} playsInline />
          <div className="scanner-frame" />
          <div className="scanner-tip">{scannerMsg}</div>
        </div>
      </Popup>
    </div>
  )
}

function LoginPage({ onLogin }) {
  return (
    <div className="login-screen">
      <div className="login-hero">
        <h1>欢迎使用</h1>
        <p>Yodex移动应用</p>
      </div>
      <Card className="login-card">
        <Form layout="vertical" onFinish={onLogin} footer={<Button block color="primary" size="large" type="submit">登录</Button>}>
          <Form.Item name="login" label="操作员" rules={[{ required: true, message: '请输入操作员' }]}>
            <Input placeholder="操作员编号 / 姓名" clearable />
          </Form.Item>
          <Form.Item name="password" label="密码" rules={[{ required: true, message: '请输入密码' }]}>
            <Input type="password" placeholder="请输入密码" clearable />
          </Form.Item>
        </Form>
      </Card>
    </div>
  )
}

function HomePage({ operator, openCheck, logout }) {
  const sections = [
    {
      title: '库存',
      items: [
        { title: '盘点单', icon: <AppOutline />, color: 'blue', onClick: openCheck },
      ],
    },
  ]
  return (
    <div className="home-shell">
      <NavBar
        backIcon={false}
        right={<Button fill="none" className="nav-icon-button logout-button" aria-label="退出登录" onClick={logout}><CloseCircleOutline /></Button>}
      >
        功能
      </NavBar>
      <div className="home-content">
        {sections.map(section => (
          <Card className="home-section-card" key={section.title}>
            <h2>{section.title}</h2>
            <div className="home-menu-grid">
              {section.items.map(item => (
                <button className="home-menu-item" type="button" key={item.title} onClick={item.onClick}>
                  <span className={`home-menu-icon ${item.color}`}>{item.icon}</span>
                  <span>{item.title}</span>
                </button>
              ))}
            </div>
          </Card>
        ))}
      </div>
    </div>
  )
}

function CheckPage(props) {
  const {
    form,
    warehouses,
    check,
    items,
    totalProfit,
    scanKeyword,
    setScanKeyword,
    chooseWarehouse,
    searchScan,
    openScanner,
    openSelect,
    openHistory,
    backHome,
    updateItemUnitCount,
    openBatch,
    removeItem,
    itemCheckedBase,
    itemStock,
    scanFlash,
    submit,
  } = props
  const warehouseDropdownRef = useRef(null)
  const hardwareScanRef = useRef(null)
  const scanBufferRef = useRef('')
  const scanBufferTimerRef = useRef(null)
  const selectedWarehouseName = warehouses.find(w => w.kTypeID === check?.ktypeid)?.fullName || '请选择仓库'
  const selectWarehouse = ktypeid => {
    warehouseDropdownRef.current?.close()
    chooseWarehouse(ktypeid)
  }
  const submitScan = async value => {
    const ok = await searchScan(value)
    if (ok) setScanKeyword('')
  }
  const submitHardwareScan = async value => {
    const keyword = value.trim()
    if (!keyword) return
    const ok = await searchScan(keyword)
    if (ok) setScanKeyword('')
  }

  useEffect(() => {
    if (!check?.checkDate || check?.ended) return undefined

    const submitBufferedScan = () => {
      const keyword = scanBufferRef.current.trim()
      scanBufferRef.current = ''
      if (keyword.length >= 3) submitHardwareScan(keyword)
    }

    const handleHardwareKey = event => {
      const target = event.target
      const tagName = target?.tagName?.toLowerCase()
      if (target === hardwareScanRef.current) return
      if (tagName === 'input') return
      if (tagName === 'textarea' || target?.isContentEditable) return

      if (event.key === 'Enter' || event.key === 'Tab') {
        if (scanBufferRef.current) {
          event.preventDefault()
          submitBufferedScan()
        }
        return
      }

      if (event.key.length !== 1) return
      scanBufferRef.current += event.key
      if (scanBufferTimerRef.current) window.clearTimeout(scanBufferTimerRef.current)
      scanBufferTimerRef.current = window.setTimeout(submitBufferedScan, 180)
    }

    window.addEventListener('keydown', handleHardwareKey, true)
    return () => {
      window.removeEventListener('keydown', handleHardwareKey, true)
      if (scanBufferTimerRef.current) window.clearTimeout(scanBufferTimerRef.current)
      scanBufferRef.current = ''
    }
  }, [check?.checkDate, check?.ended])

  useEffect(() => {
    if (!scanFlash?.pTypeID) return
    document.getElementById(itemKey(scanFlash.pTypeID))?.scrollIntoView({ block: 'center', behavior: 'smooth' })
  }, [scanFlash?.pTypeID, scanFlash?.token, items.length])

  return (
    <div className="page-shell">
      <NavBar
        backIcon={<LeftOutline />}
        onBack={backHome}
        right={(
          <Button fill="none" className="nav-icon-button" onClick={openHistory}>
            <UnorderedListOutline />
          </Button>
        )}
      >
        盘点单
      </NavBar>
      <Form form={form} layout="horizontal" className="dense-form">
        <Form.Item name="warehouse" label="仓库" required>
          <Dropdown ref={warehouseDropdownRef} className="warehouse-dropdown" closeOnMaskClick>
            <Dropdown.Item key="warehouse" title={selectedWarehouseName}>
              <List className="warehouse-menu">
                {warehouses.map(w => (
                  <List.Item key={w.kTypeID} clickable onClick={() => selectWarehouse(w.kTypeID)} extra={check?.ktypeid === w.kTypeID ? '已选' : null}>
                    {w.fullName}
                  </List.Item>
                ))}
              </List>
            </Dropdown.Item>
          </Dropdown>
        </Form.Item>
        <Form.Item label="盘点方式" required>
          <span className="readonly-text">分量盘点</span>
        </Form.Item>
        <Form.Item name="checkDate" label="盘点日期" required>
          <span className="readonly-text check-date-text">{check?.checkDate || check?.message || '选择仓库后带出'}</span>
        </Form.Item>
      </Form>

      {check?.checkDate && !check?.ended && (
        <div className="sticky-tools">
          <input
            ref={hardwareScanRef}
            className="hardware-scan-input"
            inputMode="none"
            autoComplete="off"
            onKeyDown={event => {
              if (event.key === 'Enter') {
                event.preventDefault()
                submitHardwareScan(event.currentTarget.value)
                event.currentTarget.value = ''
              }
            }}
            aria-hidden="true"
          />
          <SearchBar
            value={scanKeyword}
            onChange={setScanKeyword}
            onSearch={submitScan}
            placeholder="扫码或输入条码"
          />
          <Button className="tool-button" fill="outline" color="primary" onClick={openScanner}>扫码</Button>
          <Button className="tool-button" color="primary" onClick={openSelect}>选商品</Button>
        </div>
      )}

      <div className="content-list check-list">
        {items.length === 0 ? (
          <Empty description="请选择或扫码商品" />
        ) : (
          items.map((item, index) => {
            const stock = itemStock(item)
            const checked = itemCheckedBase(item)
            const profit = checked - stock
            const isBatch = item.isBatchManaged || item.batches.some(b => !b.deleted)
            const isScanFlash = scanFlash?.pTypeID === item.goods.pTypeID
            return (
              <div
                key={`${item.goods.pTypeID}-${isScanFlash ? scanFlash.token : 'idle'}`}
                id={itemKey(item.goods.pTypeID)}
                className={`goods-card-shell${isScanFlash ? ' goods-card-scan-flash' : ''}`}
              >
                <Card className="goods-card">
                  <div className="goods-title-row">
                    <div className="goods-title-main">
                      <div className="goods-name-line">
                        <span className="item-index-badge">{items.length - index}</span>
                        <span className="goods-name">{item.goods.fullName}</span>
                      </div>
                      <div className="goods-code">{item.goods.userCode || item.goods.pTypeID}</div>
                    </div>
                    <Button fill="none" className="delete-action" onClick={() => removeItem(index)}>
                      <DeleteOutline />
                    </Button>
                  </div>
                  <div className="goods-grid">
                    <div className="goods-meta">
                      <div className="muted-line">{item.goods.unitText || item.goods.name || '-'}</div>
                      <div><Tag color="primary" fill="outline">账面</Tag> {fmt(stock)}</div>
                      <div>盈亏：<span className={profit < 0 ? 'danger-text' : 'success-text'}>{fmt(profit)}</span></div>
                    </div>
                    <div className="goods-control">
                      {isBatch ? (
                        <Button className="batch-text-button" color="primary" onClick={() => openBatch(index)}>批次</Button>
                      ) : (
                        <div className="unit-stack">
                          {item.units.map(unit => (
                            <div className="unit-row" key={unit.ordid}>
                              <span>{unitName(unit)}</span>
                              <QuantityStepper min={0} value={Number(item.unitCounts?.[unit.ordid] || 0)} onChange={value => updateItemUnitCount(item.goods.pTypeID, unit.ordid, value)} />
                            </div>
                          ))}
                        </div>
                      )}
                    </div>
                  </div>
                </Card>
              </div>
            )
          })
        )}
      </div>

      <div className="bottom-bar">
        <div>盈亏合计：<span className={totalProfit < 0 ? 'danger-text' : 'success-text'}>{fmt(totalProfit)}</span></div>
        <Button color="primary" size="large" disabled={check?.ended} onClick={submit}>保存</Button>
      </div>
    </div>
  )
}

function SelectPage({ categories, category, setCategory, products, keyword, setKeyword, searchProducts, items, setPage, addGoods, setGoodsUnitCount, openScanner }) {
  return (
    <div className="page-shell select-shell">
      <NavBar onBack={() => setPage('check')}>选择商品</NavBar>
      <div className="select-search">
        <SearchBar value={keyword} onChange={setKeyword} onSearch={searchProducts} placeholder="请输入商品名称/编号/条码/关键字" />
        <Button fill="none" className="select-scan-button" onClick={openScanner}><ScanCodeOutline /></Button>
      </div>
      <div className="picker-layout">
        <SideBar activeKey={category} onChange={setCategory}>
          {categories.map(c => <SideBar.Item key={c.pTypeID} title={c.fullName} />)}
        </SideBar>
        <div className="product-pane">
          {products.length === 0 ? <Empty description="没有找到商品" /> : products.map((row, index) => {
            const goods = row.goods
            const item = items.find(i => i.goods.pTypeID === goods.pTypeID)
            const profit = item ? itemProfit(item) : 0
            return (
              <Card key={goods.pTypeID} className="product-card">
                <div className="goods-title-row">
                  <div>
                    <div className="goods-name">{goods.fullName}</div>
                    <div className="goods-code">{goods.userCode || goods.pTypeID}</div>
                    <div className="muted-line">{goods.unitText || goods.name || '-'}</div>
                  </div>
                </div>
                <div className="goods-grid">
                  <div className="goods-meta">
                    <div><Tag color="primary" fill="outline">账面</Tag> {fmt(row.stockQty)}</div>
                    {item && <div>盈亏：<span className={profit < 0 ? 'danger-text' : 'success-text'}>{fmt(profit)}</span></div>}
                  </div>
                  <div className="goods-control">
                    {goods.hasBatch ? (
                      <Button className="batch-text-button" color="primary" onClick={() => addGoods(goods, 'batch')}>批次</Button>
                    ) : (
                      <div className="unit-stack">
                        {goods.units.map(unit => (
                          <div className="unit-row" key={unit.ordid}>
                            <span>{unitName(unit)}</span>
                            <QuantityStepper min={0} value={Number(item?.unitCounts?.[unit.ordid] || 0)} onChange={value => setGoodsUnitCount(goods, unit, value)} />
                          </div>
                        ))}
                      </div>
                    )}
                  </div>
                </div>
              </Card>
            )
          })}
        </div>
      </div>
      <div className="bottom-bar two-col">
        <span />
        <Button block color="primary" size="large" onClick={() => setPage('check')}>选好了</Button>
      </div>
    </div>
  )
}

function itemProfit(item) {
  const stock = item.batches.length ? item.batches.filter(b => !b.deleted).reduce((sum, b) => sum + Number(b.stockQty || 0), 0) : Number(item.stockQty || 0)
  const checked = item.batches.length
    ? item.batches.filter(b => !b.deleted).reduce((sum, b) => sum + batchCountBase(b, item.units, item.unitOrdid), 0)
    : unitCountsBase(item.unitCounts, item.units)
  return checked - stock
}

function BatchPage({ item, batches, setPage, updateBatchUnitCount, setEditingBatches, confirmBatch, setBatchPopupVisible }) {
  if (!item) return null
  const active = batches.filter(b => !b.deleted)
  const visibleBatches = batches
    .map((batch, index) => ({ batch, index }))
    .filter(row => !row.batch.deleted)
  const unit = baseUnit(item.units)
  const total = active.reduce((sum, b) => sum + batchCountBase(b, item.units, item.unitOrdid), 0)
  return (
    <div className="page-shell batch-shell">
      <NavBar onBack={() => setPage('select')}>批号选择</NavBar>
      <Card className="batch-product-card">
        <div className="goods-name">{item.goods.fullName}</div>
        <div className="muted-line">{item.goods.unitText || item.goods.name || '-'}</div>
        <div className="batch-total-inline">数量：{fmt(total)}{unitName(unit)}</div>
      </Card>
      <div className="batch-list">
        {visibleBatches.length === 0 ? (
          <Empty description="没有更多数据了" />
        ) : (
          visibleBatches.map(({ batch, index }) => (
            <Card className="batch-card" key={`${batch.goodsBatchID}-${index}`}>
              <div className="batch-title-row">
                <div className="batch-title">批号：{batchTitleText(batch)}</div>
                {batch.isNew && (
                  <Button fill="none" className="delete-action" onClick={() => setEditingBatches(prev => prev.filter((_, i) => i !== index))}>
                    <DeleteOutline />
                  </Button>
                )}
              </div>
              <div className="batch-entry-grid">
                <div className="batch-info">
                  <span>生产日期：{batch.outFactoryDate || '-'}</span>
                  <span>到期日期：{batch.usefulEndDate || '-'}</span>
                  <span>账面库存：{fmt(batch.stockQty)}</span>
                  <span>辅助库存：{fmt(batch.stockPgHolInqty)}</span>
                </div>
                <div className="unit-stack batch-unit-list">
                  {item.units.map(unit => (
                    <div className="unit-row" key={unit.ordid}>
                      <span>{unitName(unit)}</span>
                      <QuantityStepper min={0} value={Number(batch.unitCounts?.[unit.ordid] || 0)} onChange={value => updateBatchUnitCount(index, unit.ordid, value)} />
                    </div>
                  ))}
                </div>
              </div>
            </Card>
          ))
        )}
      </div>
      <div className="bottom-bar batch-actions">
        <Button block fill="outline" color="primary" size="large" onClick={() => setBatchPopupVisible(true)}>+添加批号</Button>
        <Button block color="primary" size="large" onClick={confirmBatch}>确定</Button>
      </div>
    </div>
  )
}

function HistoryPage({ rows, setPage, refresh, loadHistoryToCheck, deleteHistory }) {
  const stopRowEvent = event => {
    event.stopPropagation()
  }

  return (
    <div className="page-shell">
      <NavBar
        onBack={() => setPage('check')}
        right={(
          <Button fill="none" className="nav-icon-button" onClick={refresh}>
            <RedoOutline />
          </Button>
        )}
      >
        历史记录
      </NavBar>
      <List>
        {rows.map(row => (
          <List.Item
            key={row.submitID}
            description={`${row.operatorName} · ${new Date(row.submittedAt).toLocaleString()} · ${row.batchCount} 条`}
            arrow={false}
            extra={(
              <Button
                fill="none"
                className="delete-action history-delete"
                onPointerDown={stopRowEvent}
                onMouseDown={stopRowEvent}
                onTouchStart={stopRowEvent}
                onClick={event => {
                  stopRowEvent(event)
                  deleteHistory(row)
                }}
              >
                <DeleteOutline />
              </Button>
            )}
            clickable
            onClick={() => loadHistoryToCheck(row)}
          >
            <span className="history-row-title">{row.warehouseName} · {row.checkDate}</span>
          </List.Item>
        ))}
      </List>
    </div>
  )
}

export default App
