import { expect, test } from 'vitest'
import { productName } from './config'

test('bootstrapped product name', () => {
  expect(productName).toBe('QuantDesk')
})