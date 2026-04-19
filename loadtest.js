import http from 'k6/http';
import { check, sleep } from 'k6';

export const options = {
    vus: 100,              // 100 virtual users
    duration: '30s',       // run for 30 seconds
};

// These are the short codes we seeded
const codes = ['2', '3', '4', '5', '6', '7'];

export default function () {
    // Pick a random short code — simulates real traffic pattern
    const code = codes[Math.floor(Math.random() * codes.length)];

    const res = http.get(`http://localhost:5111/${code}`, {
        redirects: 0,      // don't follow redirects, just measure the response
    });

    check(res, {
        'status is 302': (r) => r.status === 302,
    });
}